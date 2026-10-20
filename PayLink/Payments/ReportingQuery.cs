using System.Globalization;
using System.Text;
using Dapper;

namespace PayLink.Payments;

public enum ColumnKind { Text, Integer, Boolean }

/// <summary>One dataset the reporting API reads: a table, the columns it may show (never secrets, IPs or gateway forms), and how its rows are tied to a mode.</summary>
public sealed record ReportingDataset(string Name, string Table, IReadOnlyDictionary<string, ColumnKind> Columns, string ModeScope, string DefaultOrder)
{
    public bool HasAmount => Columns.ContainsKey("amount");
}

public sealed class ReportingException(string message) : Exception(message);

/// <summary>
/// The read-only reporting API's query language, the same as CrudApi's list parameters: <c>filter</c> (OData-style, every column
/// qualified as dataset.column: eq ne gt ge lt le, and/or, parentheses, contains/startswith/endswith), <c>orderBy</c>
/// (column:asc|desc, comma-separated), <c>display</c>, <c>limit</c>, <c>offset</c> and <c>groupBy</c> (count, and the sum of amount).
/// Everything is checked against the dataset's columns and every value is a parameter, so a query can only read what the
/// dataset shows, in the caller's mode.
/// </summary>
public static class ReportingQuery
{
    public const int DefaultLimit = 100;
    public const int MaxLimit = 1000;

    private const string ByPayment = "payment_id IN (SELECT id FROM payments WHERE livemode = @__livemode)";

    private static Dictionary<string, ColumnKind> Cols(string text, string ints = "", string bools = "")
    {
        var i = ints.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet();
        var b = bools.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet();
        return text.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .ToDictionary(c => c, c => i.Contains(c) ? ColumnKind.Integer : b.Contains(c) ? ColumnKind.Boolean : ColumnKind.Text);
    }

    public static readonly IReadOnlyDictionary<string, ReportingDataset> Datasets = new[]
    {
        new ReportingDataset("payments", "payments", Cols(
            "id livemode status amount currency reference description customer_name customer_email customer_phone customer_id return_url metadata " +
            "created_via created_by link_id created_at updated_at expires_at paid_at attempt_count connector method succeeded_attempt_id utr " +
            "failure_reason failure_message hold_reason amount_refunded settled_at settlement_ref settlement_fee",
            ints: "amount attempt_count amount_refunded settlement_fee", bools: "livemode"), "livemode = @__livemode", "created_at:desc"),
        new ReportingDataset("attempts", "attempts", Cols(
            "id payment_id connector method status gateway_ref failure_reason gateway_code gateway_message utr created_at updated_at " +
            "provider_payment_id paid_method ip_country risk_score", ints: "risk_score"), ByPayment, "created_at:desc"),
        new ReportingDataset("refunds", "refunds", Cols(
            "id payment_id attempt_id livemode amount currency status reason connector gateway_ref gateway_message manual created_by created_at updated_at",
            ints: "amount", bools: "livemode manual"), "livemode = @__livemode", "created_at:desc"),
        new ReportingDataset("disputes", "disputes", Cols(
            "id payment_id attempt_id connector livemode gateway_ref amount currency reason status respond_by note created_at updated_at",
            ints: "amount", bools: "livemode"), "livemode = @__livemode", "created_at:desc"),
        new ReportingDataset("links", "payment_links", Cols(
            "id code livemode title description amount min_amount max_amount currency ask_email ask_phone return_url max_payments expires_at active created_at created_by updated_at",
            ints: "amount min_amount max_amount max_payments", bools: "livemode ask_email ask_phone active"), "livemode = @__livemode", "created_at:desc"),
        new ReportingDataset("events", "payment_events", Cols(
            "id payment_id attempt_id at type status_before status_after actor_type actor_name data", ints: "id"), ByPayment, "id:desc"),
    }.ToDictionary(d => d.Name);

    public sealed record Result(IReadOnlyList<Dictionary<string, object?>> Rows, bool HasMore, long? Count);

    public static async Task<Result> RunAsync(System.Data.Common.DbConnection c, ReportingDataset ds, bool livemode,
        string? filter, string? orderBy, string? display, int? limit, int? offset, string? groupBy, bool count)
    {
        var p = new DynamicParameters();
        p.Add("__livemode", livemode);
        var where = ds.ModeScope;
        if (!string.IsNullOrWhiteSpace(filter)) where += " AND (" + new FilterParser(ds, filter, p).Parse() + ")";
        var take = Math.Clamp(limit ?? DefaultLimit, 1, MaxLimit);
        var skip = Math.Max(0, offset ?? 0);

        string sql;
        string[] columns;
        if (!string.IsNullOrWhiteSpace(groupBy))
        {
            var g = Column(ds, groupBy.Trim(), "groupBy");
            var sum = ds.HasAmount && g != "amount";
            columns = sum ? [g, "count", "amount"] : [g, "count"];
            var order = OrderBy(orderBy, name => name == g || name == "count" || (name == "amount" && sum) ? name : throw new ReportingException(
                $"With groupBy, orderBy can use {g}, count{(sum ? " or amount" : "")}."), "count:desc");
            sql = $"SELECT {g}, COUNT(*) AS count{(sum ? ", COALESCE(SUM(amount), 0) AS amount" : "")} FROM {ds.Table} WHERE {where} GROUP BY {g} ORDER BY {order}";
        }
        else
        {
            columns = string.IsNullOrWhiteSpace(display)
                ? ds.Columns.Keys.ToArray()
                : display.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(c => Column(ds, c, "display")).Distinct().ToArray();
            var order = OrderBy(orderBy, name => Column(ds, name, "orderBy"), ds.DefaultOrder);
            sql = $"SELECT {string.Join(", ", columns)} FROM {ds.Table} WHERE {where} ORDER BY {order}";
        }
        // One extra row says whether there's more.
        var rows = (await c.QueryAsync($"{sql} LIMIT {take + 1} OFFSET {skip};", p)).Cast<IDictionary<string, object?>>().ToList();
        var shaped = rows.Take(take).Select(r => columns.ToDictionary(col => col, col => Shape(ds, col, r.TryGetValue(col, out var v) ? v : null))).ToList();
        long? total = count ? await c.ExecuteScalarAsync<long>(string.IsNullOrWhiteSpace(groupBy)
            ? $"SELECT COUNT(*) FROM {ds.Table} WHERE {where};"
            : $"SELECT COUNT(*) FROM (SELECT 1 AS x FROM {ds.Table} WHERE {where} GROUP BY {Column(ds, groupBy.Trim(), "groupBy")}) grouped;", p) : null;
        return new Result(shaped, rows.Count > take, total);
    }

    private static object? Shape(ReportingDataset ds, string column, object? value) =>
        value is null or DBNull ? null
        : ds.Columns.TryGetValue(column, out var kind) && kind == ColumnKind.Boolean ? value is bool b ? b : Convert.ToInt64(value) != 0
        : (ds.Columns.TryGetValue(column, out kind) && kind == ColumnKind.Integer) || column is "count" or "amount" ? Convert.ToInt64(value)
        : value;

    /// <summary>A bare or dataset-qualified column name, checked against the dataset.</summary>
    private static string Column(ReportingDataset ds, string name, string where)
    {
        var bare = name.StartsWith(ds.Name + ".", StringComparison.OrdinalIgnoreCase) ? name[(ds.Name.Length + 1)..] : name;
        return ds.Columns.ContainsKey(bare) ? bare : throw new ReportingException($"{where}: \"{name}\" isn't a column of {ds.Name}.");
    }

    private static string OrderBy(string? orderBy, Func<string, string> column, string fallback)
    {
        var parts = (string.IsNullOrWhiteSpace(orderBy) ? fallback : orderBy).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return string.Join(", ", parts.Select(part =>
        {
            var bits = part.Split(':', 2);
            var dir = bits.Length == 2 ? bits[1].ToLowerInvariant() : "asc";
            if (dir is not ("asc" or "desc")) throw new ReportingException($"orderBy: use column:asc or column:desc, not \"{part}\".");
            return $"{column(bits[0])} {dir.ToUpperInvariant()}";
        }));
    }

    /// <summary>Recursive descent over the filter: or → and → term (comparison, function, or a parenthesised filter).</summary>
    private sealed class FilterParser(ReportingDataset ds, string text, DynamicParameters p)
    {
        private readonly List<string> _tokens = Tokenize(text);
        private int _at;
        private int _params;

        public string Parse()
        {
            var sql = Or();
            if (_at < _tokens.Count) throw new ReportingException($"filter: unexpected \"{_tokens[_at]}\".");
            return sql;
        }

        private string? Peek => _at < _tokens.Count ? _tokens[_at] : null;
        private string Next() => _at < _tokens.Count ? _tokens[_at++] : throw new ReportingException("filter: it ends too early.");
        private void Expect(string t) { if (!string.Equals(Next(), t, StringComparison.OrdinalIgnoreCase)) throw new ReportingException($"filter: expected \"{t}\"."); }
        private bool Is(string t) => string.Equals(Peek, t, StringComparison.OrdinalIgnoreCase);

        private string Or()
        {
            var left = And();
            while (Is("or")) { _at++; left = $"({left} OR {And()})"; }
            return left;
        }

        private string And()
        {
            var left = Term();
            while (Is("and")) { _at++; left = $"({left} AND {Term()})"; }
            return left;
        }

        private string Term()
        {
            if (Is("(")) { _at++; var inner = Or(); Expect(")"); return $"({inner})"; }
            if (Is("not")) { _at++; return $"NOT ({Term()})"; }
            var first = Next();
            if (first.ToLowerInvariant() is "contains" or "startswith" or "endswith" && Is("("))
            {
                _at++;
                var column = Qualified(Next());
                Expect(",");
                var value = Literal(Next());
                Expect(")");
                if (value is not string s) throw new ReportingException($"filter: {first} needs a quoted text value.");
                if (ds.Columns[column] != ColumnKind.Text) throw new ReportingException($"filter: {first} works on text columns; {ds.Name}.{column} isn't one.");
                var escaped = s.Replace("!", "!!").Replace("%", "!%").Replace("_", "!_");
                var pattern = first.ToLowerInvariant() switch { "contains" => $"%{escaped}%", "startswith" => $"{escaped}%", _ => $"%{escaped}" };
                return $"LOWER({column}) LIKE LOWER({Param(pattern)}) ESCAPE '!'";
            }
            var col = Qualified(first);
            var op = Next().ToLowerInvariant();
            var literal = Literal(Next());
            if (literal is null)
                return op switch
                {
                    "eq" => $"{col} IS NULL",
                    "ne" => $"{col} IS NOT NULL",
                    _ => throw new ReportingException("filter: null only works with eq and ne."),
                };
            var kind = ds.Columns[col];
            if (kind == ColumnKind.Boolean && literal is not bool) throw new ReportingException($"filter: {ds.Name}.{col} is true or false.");
            if (kind == ColumnKind.Integer && literal is not long) throw new ReportingException($"filter: {ds.Name}.{col} is a whole number (amounts are in paise).");
            if (kind == ColumnKind.Text && literal is not string) throw new ReportingException($"filter: {ds.Name}.{col} is text; quote the value: '…'.");
            var sqlOp = op switch
            {
                "eq" => "=", "ne" => "<>", "gt" => ">", "ge" => ">=", "lt" => "<", "le" => "<=",
                _ => throw new ReportingException($"filter: unknown operator \"{op}\" (use eq, ne, gt, ge, lt, le)."),
            };
            if (kind == ColumnKind.Boolean && sqlOp is not ("=" or "<>")) throw new ReportingException("filter: true/false only works with eq and ne.");
            return $"{col} {sqlOp} {Param(literal)}";
        }

        private string Qualified(string token)
        {
            var dot = token.IndexOf('.');
            if (dot < 0) throw new ReportingException($"filter: qualify every column, e.g. {ds.Name}.{token} (\"{token}\" alone isn't accepted).");
            if (!string.Equals(token[..dot], ds.Name, StringComparison.OrdinalIgnoreCase)) throw new ReportingException($"filter: \"{token}\" isn't in {ds.Name}.");
            var column = token[(dot + 1)..];
            return ds.Columns.ContainsKey(column) ? column : throw new ReportingException($"filter: \"{token}\" isn't a column of {ds.Name}.");
        }

        private static object? Literal(string token)
        {
            if (token.Length >= 2 && token[0] == '\'' && token[^1] == '\'') return token[1..^1].Replace("''", "'");
            if (token.Equals("null", StringComparison.OrdinalIgnoreCase)) return null;
            if (token.Equals("true", StringComparison.OrdinalIgnoreCase)) return true;
            if (token.Equals("false", StringComparison.OrdinalIgnoreCase)) return false;
            if (long.TryParse(token, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var n)) return n;
            throw new ReportingException($"filter: \"{token}\" isn't a value (quote text: 'like this').");
        }

        private string Param(object value)
        {
            var name = "f" + _params++;
            p.Add(name, value);
            return "@" + name;
        }

        private static List<string> Tokenize(string text)
        {
            if (text.Length > 2000) throw new ReportingException("filter: too long.");
            var tokens = new List<string>();
            for (var i = 0; i < text.Length;)
            {
                var ch = text[i];
                if (char.IsWhiteSpace(ch)) { i++; continue; }
                if (ch is '(' or ')' or ',') { tokens.Add(ch.ToString()); i++; continue; }
                if (ch == '\'')
                {
                    var sb = new StringBuilder("'");
                    i++;
                    while (true)
                    {
                        if (i >= text.Length) throw new ReportingException("filter: a quoted value isn't closed.");
                        if (text[i] == '\'' && i + 1 < text.Length && text[i + 1] == '\'') { sb.Append("''"); i += 2; continue; }
                        if (text[i] == '\'') { sb.Append('\''); i++; break; }
                        sb.Append(text[i++]);
                    }
                    tokens.Add(sb.ToString());
                    continue;
                }
                var start = i;
                while (i < text.Length && !char.IsWhiteSpace(text[i]) && text[i] is not ('(' or ')' or ',' or '\'')) i++;
                tokens.Add(text[start..i]);
            }
            if (tokens.Count > 200) throw new ReportingException("filter: too many conditions.");
            return tokens;
        }
    }
}
