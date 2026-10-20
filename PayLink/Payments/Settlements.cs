using System.Globalization;
using Dapper;
using PayLink.Infrastructure;
using PayLink.Platform;
using PayLink.Tenancy;

namespace PayLink.Payments;

public sealed class SettlementBatch
{
    public long Id { get; set; }
    public string? Connector { get; set; }
    public string FileName { get; set; } = "";
    public string UploadedAt { get; set; } = "";
    public string? UploadedBy { get; set; }
    public int LineCount { get; set; }
    public int Matched { get; set; }
    public int Mismatched { get; set; }
    public int Unmatched { get; set; }
}

public sealed class SettlementLine
{
    public long Id { get; set; }
    public long BatchId { get; set; }
    public string Reference { get; set; } = "";
    public long Gross { get; set; }
    public long Fee { get; set; }
    public long Tax { get; set; }
    public long Net { get; set; }
    public string? SettledOn { get; set; }
    public string? SettlementRef { get; set; }
    public string? PaymentId { get; set; }
    public string MatchStatus { get; set; } = "";
    public string? Note { get; set; }
}

/// <summary>Which column holds what, in an uploaded settlement report.</summary>
public sealed record SettlementMapping(int Reference, int Gross, int? Fee, int? Tax, int? Net, int? Date, int? SettlementRef, bool AmountsInPaise);

/// <summary>
/// Settlement reconciliation: a gateway's settlement report (CSV) is matched line by line to payments, by the gateway's
/// payment id, PayLink's attempt or payment id, or the UTR. A line whose gross differs from the payment is flagged;
/// one that matches nothing is listed. Matched payments show when they were settled and the fee.
/// </summary>
public sealed class Settlements(TenantDb tenantDb, AppPaths paths)
{
    private static readonly string[] RefHeaders = ["paymentid", "entityid", "mihpayid", "transactionid", "txnid", "orderid", "trackingid", "referenceno", "utr", "rrn", "merchantorderid", "id"];
    private static readonly string[] GrossHeaders = ["amount", "grossamount", "gross", "transactionamount", "orderamount", "txnamount"];
    private static readonly string[] FeeHeaders = ["fee", "fees", "commission", "mdr", "servicefee", "gatewayfee", "charges"];
    private static readonly string[] TaxHeaders = ["tax", "gst", "servicetax", "igst"];
    private static readonly string[] NetHeaders = ["net", "netamount", "settledamount", "settlementamount", "credit", "payoutamount"];
    private static readonly string[] DateHeaders = ["settledat", "settlementdate", "settledon", "payoutdate", "valuedate", "date"];
    private static readonly string[] SettlementRefHeaders = ["settlementid", "settlementutr", "settlementreference", "payoutid", "utrno"];

    private static string Norm(string header) => new(header.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());

    public static SettlementMapping Guess(IReadOnlyList<string> headers)
    {
        var n = headers.Select(Norm).ToList();
        int? Find(string[] names) { foreach (var name in names) { var i = n.IndexOf(name); if (i >= 0) return i; } return null; }
        return new SettlementMapping(Find(RefHeaders) ?? 0, Find(GrossHeaders) ?? Math.Min(1, headers.Count - 1), Find(FeeHeaders), Find(TaxHeaders),
            Find(NetHeaders), Find(DateHeaders), Find(SettlementRefHeaders), AmountsInPaise: false);
    }

    private string Folder => Path.Combine(paths.DataRoot, "files", "uploads");

    /// <summary>Keeps an uploaded report for the mapping step; returns its token and header row, or a problem.</summary>
    public async Task<(string? Token, IReadOnlyList<string> Headers, string? Problem)> StageAsync(long tenantId, Stream csv, string fileName)
    {
        using var reader = new StreamReader(csv);
        var text = await reader.ReadToEndAsync();
        var rows = Csv.Parse(text);
        if (rows.Count < 2) return (null, [], "The file has no rows below its header.");
        Directory.CreateDirectory(Folder);
        var token = $"{tenantId}_{Guid.NewGuid():N}";
        await File.WriteAllTextAsync(Path.Combine(Folder, token + ".csv"), text);
        await File.WriteAllTextAsync(Path.Combine(Folder, token + ".name"), Path.GetFileName(fileName));
        return (token, rows[0], null);
    }

    public IReadOnlyList<string>? StagedHeaders(long tenantId, string token) =>
        StagedFile(tenantId, token) is { } f ? Csv.Parse(File.ReadAllText(f)).FirstOrDefault() : null;

    private string? StagedFile(long tenantId, string token)
    {
        if (!token.StartsWith(tenantId + "_", StringComparison.Ordinal) || token.Any(ch => !char.IsLetterOrDigit(ch) && ch != '_')) return null;
        var f = Path.Combine(Folder, token + ".csv");
        return File.Exists(f) ? f : null;
    }

    public static long? Amount(string? text, bool paise)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var clean = new string(text.Where(ch => char.IsDigit(ch) || ch is '.' or '-').ToArray());
        if (!decimal.TryParse(clean, NumberStyles.Number, CultureInfo.InvariantCulture, out var v)) return null;
        return paise ? (long)Math.Round(v) : (long)Math.Round(v * 100m, MidpointRounding.AwayFromZero);
    }

    private static string? Date(string? text) =>
        string.IsNullOrWhiteSpace(text) ? null
        : DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var d) ? d.ToString("yyyy-MM-dd")
        : DateTime.TryParseExact(text.Trim(), ["dd/MM/yyyy", "dd-MM-yyyy", "dd/MM/yyyy HH:mm:ss", "dd-MM-yyyy HH:mm:ss", "d MMM yyyy"], CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var e) ? e.ToString("yyyy-MM-dd")
        : null;

    public async Task<(SettlementBatch? Batch, string? Problem)> ImportAsync(TenantRecord tenant, string token, string? connector, SettlementMapping map, Actor actor)
    {
        if (StagedFile(tenant.Id, token) is not { } file) return (null, "That upload has expired; upload the file again.");
        var rows = Csv.Parse(await File.ReadAllTextAsync(file));
        var name = File.Exists(Path.ChangeExtension(file, ".name")) ? await File.ReadAllTextAsync(Path.ChangeExtension(file, ".name")) : "settlement.csv";
        string? Cell(List<string> r, int? i) => i is { } x && x >= 0 && x < r.Count ? r[x].Trim() : null;

        await using var c = await tenantDb.OpenAsync(tenant);
        var batch = new SettlementBatch { Connector = string.IsNullOrWhiteSpace(connector) ? null : connector, FileName = name, UploadedAt = Clock.Now(), UploadedBy = actor.Name };
        await c.ExecuteAsync("INSERT INTO settlement_batches (connector, file_name, uploaded_at, uploaded_by) VALUES (@Connector, @FileName, @UploadedAt, @UploadedBy);", batch);
        batch.Id = await c.ExecuteScalarAsync<long>(Db(tenant).LastInsertId);

        foreach (var r in rows.Skip(1))
        {
            var reference = Cell(r, map.Reference);
            var gross = Amount(Cell(r, map.Gross), map.AmountsInPaise);
            if (string.IsNullOrEmpty(reference) || gross is null) continue;
            var fee = Amount(Cell(r, map.Fee), map.AmountsInPaise) ?? 0;
            var tax = Amount(Cell(r, map.Tax), map.AmountsInPaise) ?? 0;
            var line = new SettlementLine
            {
                BatchId = batch.Id, Reference = reference.Length > 200 ? reference[..200] : reference, Gross = gross.Value, Fee = fee, Tax = tax,
                Net = Amount(Cell(r, map.Net), map.AmountsInPaise) ?? gross.Value - fee - tax, SettledOn = Date(Cell(r, map.Date)),
                SettlementRef = Cell(r, map.SettlementRef) is { Length: > 0 } sr ? (sr.Length > 100 ? sr[..100] : sr) : null,
            };

            var paymentId = await c.ExecuteScalarAsync<string?>(
                "SELECT payment_id FROM attempts WHERE provider_payment_id = @reference OR gateway_ref = @reference OR id = @reference LIMIT 1;", new { reference = line.Reference })
                ?? await c.ExecuteScalarAsync<string?>("SELECT id FROM payments WHERE id = @reference OR utr = @reference LIMIT 1;", new { reference = line.Reference });
            var payment = paymentId is null ? null : await c.QueryFirstOrDefaultAsync<Payment>("SELECT * FROM payments WHERE id = @paymentId;", new { paymentId });
            if (payment is null) { line.MatchStatus = "unmatched"; batch.Unmatched++; }
            else if (await c.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM settlement_lines WHERE payment_id = @Id AND match_status = 'matched';", new { payment.Id }) > 0)
            { line.MatchStatus = "duplicate"; line.PaymentId = payment.Id; line.Note = "Already settled in an earlier report"; batch.Mismatched++; }
            else if (payment.Status != PaymentStatuses.Succeeded)
            { line.MatchStatus = "amount_mismatch"; line.PaymentId = payment.Id; line.Note = $"The payment is {payment.Status} in PayLink"; batch.Mismatched++; }
            else if (payment.Amount != line.Gross)
            { line.MatchStatus = "amount_mismatch"; line.PaymentId = payment.Id; line.Note = $"PayLink has {Money.Format(payment.Amount, payment.Currency)}, the report {Money.Format(line.Gross, payment.Currency)}"; batch.Mismatched++; }
            else
            {
                line.MatchStatus = "matched"; line.PaymentId = payment.Id; batch.Matched++;
                await c.ExecuteAsync("UPDATE payments SET settled_at = @at, settlement_ref = @ref, settlement_fee = @fee WHERE id = @Id;",
                    new { at = line.SettledOn ?? Clock.Now()[..10], @ref = line.SettlementRef, fee = line.Fee + line.Tax, payment.Id });
            }
            await c.ExecuteAsync(
                "INSERT INTO settlement_lines (batch_id, reference, gross, fee, tax, net, settled_on, settlement_ref, payment_id, match_status, note) " +
                "VALUES (@BatchId, @Reference, @Gross, @Fee, @Tax, @Net, @SettledOn, @SettlementRef, @PaymentId, @MatchStatus, @Note);", line);
            batch.LineCount++;
        }
        await c.ExecuteAsync("UPDATE settlement_batches SET line_count = @LineCount, matched = @Matched, mismatched = @Mismatched, unmatched = @Unmatched WHERE id = @Id;", batch);
        await AuditLog.WriteAsync(c, actor, "settlement.imported", name, $"{batch.LineCount} lines: {batch.Matched} matched, {batch.Mismatched} mismatched, {batch.Unmatched} unmatched");
        try { File.Delete(file); File.Delete(Path.ChangeExtension(file, ".name")); } catch (IOException) { }
        return (batch, null);
    }

    public async Task<IReadOnlyList<SettlementBatch>> BatchesAsync(TenantRecord t)
    {
        await using var c = await tenantDb.OpenAsync(t);
        return (await c.QueryAsync<SettlementBatch>("SELECT * FROM settlement_batches ORDER BY id DESC LIMIT 20;")).ToList();
    }

    public async Task<IReadOnlyList<SettlementLine>> ProblemsAsync(TenantRecord t, long? batchId = null)
    {
        await using var c = await tenantDb.OpenAsync(t);
        return (await c.QueryAsync<SettlementLine>(
            $"SELECT * FROM settlement_lines WHERE match_status <> 'matched' {(batchId is null ? "" : "AND batch_id = @batchId")} ORDER BY id DESC LIMIT 200;", new { batchId })).ToList();
    }

    /// <summary>Settable properties, not a positional record: SQLite types an empty aggregate as BLOB, which Dapper can't match to a constructor.</summary>
    public sealed class CloseDay
    {
        public string Day { get; set; } = "";
        public long Lines { get; set; }
        public long Gross { get; set; }
        public long Fees { get; set; }
        public long Tax { get; set; }
        public long Net { get; set; }
    }

    /// <summary>The daily close: what was settled each day, and succeeded gateway payments still unsettled after <paramref name="days"/> days.</summary>
    public async Task<(IReadOnlyList<CloseDay> Days, IReadOnlyList<Payment> Unsettled)> CloseAsync(TenantRecord t, int days = 3)
    {
        await using var c = await tenantDb.OpenAsync(t);
        var close = (await c.QueryAsync<CloseDay>(
            "SELECT settled_on AS Day, COUNT(*) AS Lines, SUM(gross) AS Gross, SUM(fee) AS Fees, SUM(tax) AS Tax, SUM(net) AS Net FROM settlement_lines " +
            "WHERE match_status = 'matched' AND settled_on IS NOT NULL GROUP BY settled_on ORDER BY settled_on DESC LIMIT 31;")).ToList();
        var unsettled = (await c.QueryAsync<Payment>(
            "SELECT * FROM payments WHERE status = 'succeeded' AND livemode = TRUE AND settled_at IS NULL AND connector NOT IN ('upi', 'simulator') AND paid_at < @before ORDER BY paid_at LIMIT 100;",
            new { before = Clock.Format(DateTime.UtcNow.AddDays(-days)) })).ToList();
        return (close, unsettled);
    }

    private WebSvc.PayLink.Storage.SqlDialect Db(TenantRecord t) => WebSvc.PayLink.Storage.Db.Dialect(tenantDb.Target(t).Engine);
}
