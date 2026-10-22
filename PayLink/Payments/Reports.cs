using Dapper;
using PayLink.Infrastructure;
using PayLink.Platform;
using PayLink.Tenancy;

namespace PayLink.Payments;

/// <summary>A tenant's reports: the checkout funnel, success by method, why payments fail, time to pay, daily totals.</summary>
public sealed class Reports(TenantDb tenantDb, PaymentService payments)
{
    public sealed record Funnel(long Created, long Opened, long Chose, long Succeeded);
    public sealed record ByConnector(string Connector, long Attempts, long Succeeded, double? MedianSeconds);
    public sealed record FailureRow(string Reason, string Connector, long Count);
    public sealed record Day(DateOnly Date, long Count, long Amount);
    public sealed record Report(Funnel Funnel, IReadOnlyList<ByConnector> Connectors, IReadOnlyList<FailureRow> Failures,
        double? MedianTimeToPaySeconds, IReadOnlyList<Day> Days, long Refunded);

    public Task<Report> BuildAsync(TenantRecord tenant, bool livemode, int days) =>
        BuildAsync(tenant, livemode, DateRange.Parse(days.ToString(), null, null));

    public async Task<Report> BuildAsync(TenantRecord tenant, bool livemode, DateRange range)
    {
        var since = range.From.ToDateTime(TimeOnly.MinValue);
        var days = range.Days;
        var s = range.Start;
        var e = range.End;
        await using var c = await tenantDb.OpenAsync(tenant);
        var created = await c.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM payments WHERE livemode = @livemode AND created_at >= @s AND created_at < @e;", new { livemode, s, e });
        var opened = await c.ExecuteScalarAsync<long>(
            "SELECT COUNT(DISTINCT e.payment_id) FROM payment_events e JOIN payments p ON p.id = e.payment_id WHERE e.type = 'checkout.opened' AND p.livemode = @livemode AND p.created_at >= @s AND p.created_at < @e;", new { livemode, s, e });
        var chose = await c.ExecuteScalarAsync<long>(
            "SELECT COUNT(DISTINCT a.payment_id) FROM attempts a JOIN payments p ON p.id = a.payment_id WHERE p.livemode = @livemode AND p.created_at >= @s AND p.created_at < @e;", new { livemode, s, e });
        var paid = (await c.QueryAsync<(string CreatedAt, string PaidAt, long Amount)>(
            "SELECT created_at, paid_at, amount FROM payments WHERE livemode = @livemode AND created_at >= @s AND created_at < @e AND status = 'succeeded' AND paid_at IS NOT NULL;", new { livemode, s, e })).ToList();
        var attempts = (await c.QueryAsync<(string Connector, string Status, string CreatedAt, string UpdatedAt, string? FailureReason)>(
            "SELECT a.connector, a.status, a.created_at, a.updated_at, a.failure_reason FROM attempts a JOIN payments p ON p.id = a.payment_id WHERE p.livemode = @livemode AND a.created_at >= @s AND a.created_at < @e;",
            new { livemode, s, e })).ToList();
        var refunded = await c.ExecuteScalarAsync<long>(
            "SELECT COALESCE(SUM(r.amount), 0) FROM refunds r WHERE r.livemode = @livemode AND r.status = 'succeeded' AND r.created_at >= @s AND r.created_at < @e;", new { livemode, s, e });

        static double? Median(IEnumerable<double> values)
        {
            var list = values.OrderBy(v => v).ToList();
            return list.Count == 0 ? null : list[list.Count / 2];
        }
        static double Seconds(string from, string to) => ((Clock.Parse(to) ?? DateTime.UtcNow) - (Clock.Parse(from) ?? DateTime.UtcNow)).TotalSeconds;

        var connectors = attempts.GroupBy(a => a.Connector).Select(g => new ByConnector(g.Key, g.Count(), g.Count(a => a.Status == "succeeded"),
            Median(g.Where(a => a.Status == "succeeded").Select(a => Seconds(a.CreatedAt, a.UpdatedAt))))).OrderByDescending(r => r.Attempts).ToList();
        var failures = attempts.Where(a => a.Status is "failed" or "cancelled" or "expired")
            .GroupBy(a => (Reason: a.FailureReason ?? a.Status, a.Connector)).Select(g => new FailureRow(g.Key.Reason, g.Key.Connector, g.Count()))
            .OrderByDescending(f => f.Count).ToList();
        var byDay = paid.GroupBy(p => DateOnly.FromDateTime(Clock.Parse(p.PaidAt)!.Value)).ToDictionary(g => g.Key, g => (Count: (long)g.Count(), Amount: g.Sum(x => x.Amount)));
        var daysList = Enumerable.Range(0, days).Select(i => DateOnly.FromDateTime(since.AddDays(i)))
            .Select(d => new Day(d, byDay.GetValueOrDefault(d).Count, byDay.GetValueOrDefault(d).Amount)).ToList();
        return new Report(new Funnel(created, opened, chose, paid.Count), connectors, failures, Median(paid.Select(p => Seconds(p.CreatedAt, p.PaidAt))), daysList, refunded);
    }

    /// <summary>Payments created in the period, as CSV (amounts in rupees), for spreadsheets and accounting.</summary>
    public Task<string> CsvAsync(TenantRecord tenant, bool livemode, int days) =>
        CsvAsync(tenant, livemode, new PaymentFilter(Dates: DateRange.Parse(days.ToString(), null, null)));

    /// <summary>The payments a filter matches (up to 50,000, oldest first), as CSV.</summary>
    public async Task<string> CsvAsync(TenantRecord tenant, bool livemode, PaymentFilter filter)
    {
        var rows = (await payments.SearchAsync(tenant, livemode, filter, 50_000)).OrderBy(p => p.CreatedAt);
        var sb = new System.Text.StringBuilder("id,created_at,status,amount,currency,refunded,reference,description,customer_name,customer_email,customer_phone,method,connector,utr,paid_at,failure_reason,settled_at,settlement_ref\n");
        static string F(object? v)
        {
            var t = v?.ToString() ?? "";
            if (t.Length > 0 && "=+-@".Contains(t[0])) t = "'" + t;   // keep spreadsheets from running formulas
            return t.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? "\"" + t.Replace("\"", "\"\"") + "\"" : t;
        }
        foreach (var p in rows)
            sb.AppendLine(string.Join(",", new object?[]
            {
                p.Id, p.CreatedAt, p.Status, (p.Amount / 100m).ToString("0.00", System.Globalization.CultureInfo.InvariantCulture), p.Currency,
                (p.AmountRefunded / 100m).ToString("0.00", System.Globalization.CultureInfo.InvariantCulture), p.Reference, p.Description,
                p.CustomerName, p.CustomerEmail, p.CustomerPhone, p.Method, p.Connector, p.Utr, p.PaidAt, p.FailureReason, p.SettledAt, p.SettlementRef,
            }.Select(F)));
        return sb.ToString();
    }
}
