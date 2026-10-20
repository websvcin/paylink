using System.Globalization;
using System.Text.RegularExpressions;
using Dapper;
using PayLink.Infrastructure;
using PayLink.Platform;
using PayLink.Tenancy;
using WebSvc.PayLink.Connector.Upi;

namespace PayLink.Payments;

public sealed class BankStatementLine
{
    public long Id { get; set; }
    public string UploadId { get; set; } = "";
    public int LineNo { get; set; }
    public string? TxnDate { get; set; }
    public string? Utr { get; set; }
    public long Amount { get; set; }
    public string? Narration { get; set; }
    public string Result { get; set; } = "";
    public string? PaymentId { get; set; }
    public string? Note { get; set; }
    public string CreatedAt { get; set; } = "";
}

/// <summary>
/// UPI statement matching: the merchant uploads their bank statement (CSV, as most Indian banks export it) and every credit whose UTR
/// and amount match a waiting PayLink UPI payment is approved, with the statement line (UTR, amount, date) as the note. Anything that doesn't match exactly
/// is left in the queue and listed, so a person still decides those.
/// </summary>
public sealed partial class UpiStatementService(TenantDb tenantDb, PaymentService payments)
{
    public static class Results
    {
        public const string Approved = "approved";
        public const string AmountMismatch = "amount_mismatch";
        public const string AlreadyPaid = "already_paid";
        public const string NotWaiting = "not_waiting";
        public const string NotFound = "not_found";
        public const string NoUtr = "no_utr";
    }

    public sealed record Line(int LineNo, string? Date, string? Utr, long Amount, string? Narration);
    public sealed record Outcome(string UploadId, int Credits, int Approved, int NeedALook, int NotFound, int NoUtr);

    [GeneratedRegex(@"(?<!\d)(\d{12})(?!\d)")]
    private static partial Regex UtrPattern();

    private static int Find(List<string> header, params string[] words) =>
        header.FindIndex(h => words.Any(w => h.Contains(w, StringComparison.OrdinalIgnoreCase)));

    /// <summary>Reads the credits from a statement. Banks put a few lines of account details above the table, so the header row is searched for.</summary>
    public static (List<Line> Lines, string? Problem) Read(string text)
    {
        var rows = Csv.Parse(text);
        var headerAt = rows.Take(40).ToList().FindIndex(r =>
            r.Count >= 3 && Find(r, "narration", "description", "particulars", "remarks", "details") >= 0 && Find(r, "credit", "deposit", "amount") >= 0);
        if (headerAt < 0) return ([], "Couldn't find the statement's columns. Export it as CSV with a header row (Date, Narration or Description, Credit or Deposit).");
        var header = rows[headerAt];
        var narration = Find(header, "narration", "description", "particulars", "remarks", "details");
        var credit = Find(header, "credit", "deposit");
        var amount = credit >= 0 ? credit : Find(header, "amount");
        var drcr = credit >= 0 ? -1 : Find(header, "cr/dr", "dr/cr", "type");
        var reference = header.FindIndex(h => Regex.IsMatch(h, @"utr|ref|cheque|chq", RegexOptions.IgnoreCase) && !h.Contains("date", StringComparison.OrdinalIgnoreCase));
        var date = Find(header, "date");

        var lines = new List<Line>();
        for (var i = headerAt + 1; i < rows.Count; i++)
        {
            var r = rows[i];
            string Cell(int col) => col >= 0 && col < r.Count ? r[col].Trim() : "";
            if (drcr >= 0 && !Cell(drcr).StartsWith("C", StringComparison.OrdinalIgnoreCase)) continue;
            var raw = Regex.Replace(Cell(amount), @"[,₹\s]|cr$|CR$|Cr$", "");
            if (!decimal.TryParse(raw, NumberStyles.Number, CultureInfo.InvariantCulture, out var value) || value <= 0) continue;
            var utr = UtrPattern().Match(Cell(reference)) is { Success: true } m1 ? m1.Groups[1].Value
                : UtrPattern().Match(Cell(narration)) is { Success: true } m2 ? m2.Groups[1].Value : null;
            var text500 = Cell(narration);
            lines.Add(new Line(i + 1, Cell(date) is { Length: > 0 } d ? d : null, utr, (long)Math.Round(value * 100m, MidpointRounding.AwayFromZero),
                text500.Length <= 500 ? text500 : text500[..500]));
        }
        return lines.Count == 0 ? (lines, "No credits found in the statement.") : (lines, null);
    }

    public async Task<(Outcome? Outcome, string? Problem)> MatchAsync(TenantRecord tenant, bool livemode, string text, EventSource source)
    {
        var (lines, problem) = Read(text);
        if (problem is not null) return (null, problem);
        var uploadId = Ids.New("stm", 12);
        var results = new List<(Line Line, string Result, string? PaymentId, string? Note)>();
        foreach (var line in lines)
        {
            if (line.Utr is null) { results.Add((line, Results.NoUtr, null, null)); continue; }
            Attempt? a;
            Payment? p;
            await using (var c = await tenantDb.OpenAsync(tenant))
            {
                a = await c.QueryFirstOrDefaultAsync<Attempt>(
                    "SELECT a.* FROM attempts a JOIN payments p ON p.id = a.payment_id WHERE a.connector = @upi AND a.utr = @utr AND p.livemode = @livemode " +
                    "ORDER BY a.created_at DESC LIMIT 1;", new { upi = UpiConnector.Id, utr = line.Utr, livemode });
                p = a is null ? null : await c.QueryFirstOrDefaultAsync<Payment>("SELECT * FROM payments WHERE id = @PaymentId;", a);
            }
            if (a is null || p is null) { results.Add((line, Results.NotFound, null, null)); continue; }
            if (a.Status == AttemptStatuses.Succeeded) { results.Add((line, Results.AlreadyPaid, p.Id, null)); continue; }
            if (a.Status != AttemptStatuses.Processing) { results.Add((line, Results.NotWaiting, p.Id, $"The payment is {PaymentStatuses.Label(p.Status).ToLowerInvariant()}.")); continue; }
            if (p.Amount != line.Amount || p.Currency != "INR")
            {
                results.Add((line, Results.AmountMismatch, p.Id, $"PayLink expects {Money.Format(p.Amount, p.Currency)}, the bank shows {Money.Format(line.Amount, "INR")}."));
                continue;
            }
            var note = $"Bank statement: UTR {line.Utr}, {Money.Format(line.Amount, "INR")}{(line.Date is null ? "" : $" on {line.Date}")}.";
            var verified = await payments.VerifyUpiAsync(tenant, a.Id, approve: true, note, source);
            results.Add(verified.Error is { } e ? (line, Results.NotWaiting, p.Id, e.Message) : (line, Results.Approved, p.Id, null));
        }

        await using (var c = await tenantDb.OpenAsync(tenant))
        {
            var now = Clock.Now();
            foreach (var (line, result, paymentId, note) in results)
                await c.ExecuteAsync(
                    "INSERT INTO bank_statement_lines (upload_id, line_no, txn_date, utr, amount, narration, result, payment_id, note, created_at, created_by) " +
                    "VALUES (@uploadId, @LineNo, @Date, @Utr, @Amount, @Narration, @result, @paymentId, @note, @now, @by);",
                    new { uploadId, line.LineNo, line.Date, line.Utr, line.Amount, line.Narration, result, paymentId, note, now, by = source.Actor.Name });
        }
        var outcome = new Outcome(uploadId, results.Count, results.Count(r => r.Result == Results.Approved),
            results.Count(r => r.Result is Results.AmountMismatch or Results.NotWaiting), results.Count(r => r.Result == Results.NotFound), results.Count(r => r.Result == Results.NoUtr));
        await tenantDb.AuditAsync(tenant, source.Actor, "upi.statement_matched", uploadId,
            $"{outcome.Credits} credits: {outcome.Approved} approved, {outcome.NeedALook} need a look, {outcome.NotFound} not PayLink payments");
        return (outcome, null);
    }

    /// <summary>Statement lines that matched a PayLink payment but couldn't be approved automatically.</summary>
    public async Task<IReadOnlyList<BankStatementLine>> NeedALookAsync(TenantRecord tenant, string? uploadId = null)
    {
        await using var c = await tenantDb.OpenAsync(tenant);
        return (await c.QueryAsync<BankStatementLine>(
            $"SELECT * FROM bank_statement_lines WHERE result IN ('amount_mismatch', 'not_waiting') {(uploadId is null ? "" : "AND upload_id = @uploadId")} ORDER BY id DESC LIMIT 100;",
            new { uploadId })).ToList();
    }
}
