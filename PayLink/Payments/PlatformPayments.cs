using Dapper;
using PayLink.Infrastructure;
using PayLink.Platform;
using PayLink.Tenancy;

namespace PayLink.Payments;

/// <summary>Questions asked across every tenant: what's still open on a connector, and the platform dashboard's numbers.</summary>
public sealed class PlatformPayments(PlatformRepository repo, TenantDb tenantDb, HealthState health, ILogger<PlatformPayments> logger)
{
    /// <summary>Open attempts and pending refunds on a connector, across all tenants. A connector can only be uninstalled at zero.</summary>
    public async Task<(long Attempts, long Refunds)> OpenOnConnectorAsync(string connectorId)
    {
        long attempts = 0, refunds = 0;
        foreach (var t in await repo.TenantsAsync(take: int.MaxValue))
        {
            if (health.DegradedTenants.Contains(t.Slug)) continue;
            try
            {
                await using var c = await tenantDb.OpenAsync(t);
                attempts += await c.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM attempts WHERE connector = @connectorId AND status IN ('requires_action', 'processing');", new { connectorId });
                refunds += await c.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM refunds WHERE connector = @connectorId AND status = 'pending';", new { connectorId });
            }
            catch (Exception ex) { logger.LogWarning(ex, "Couldn't check tenant {Tenant}", t.Slug); }
        }
        return (attempts, refunds);
    }

    public sealed record TenantVolume(TenantRecord Tenant, long Succeeded, long Amount, long Failed, long UpiWaiting);
    public sealed record Dashboard(long Succeeded, long Amount, long Failed, long Open, long UpiWaiting, long Refunded,
        IReadOnlyList<TenantVolume> Tenants, IReadOnlyDictionary<string, (long Count, long Amount)> ByConnector, DateTime At);

    private Dashboard? _cached;

    public void Forget() => _cached = null;

    /// <summary>Live payments in the last <paramref name="days"/> days across all tenants, cached for a minute.</summary>
    public async Task<Dashboard> DashboardAsync(int days = 7)
    {
        if (_cached is { } c0 && DateTime.UtcNow - c0.At < TimeSpan.FromMinutes(1)) return c0;
        var since = Clock.Format(DateTime.UtcNow.AddDays(-days));
        long succeeded = 0, amount = 0, failed = 0, open = 0, upi = 0, refunded = 0;
        var tenants = new List<TenantVolume>();
        var byConnector = new Dictionary<string, (long, long)>();
        foreach (var t in (await repo.TenantsAsync(take: 500)).Where(t => t.IsActive && !health.DegradedTenants.Contains(t.Slug)))
        {
            try
            {
                await using var c = await tenantDb.OpenAsync(t);
                var rows = (await c.QueryAsync<(string Status, string? Connector, long Count, long Amount, long Refunded)>(
                    "SELECT status, connector, COUNT(*), COALESCE(SUM(amount), 0), COALESCE(SUM(amount_refunded), 0) FROM payments " +
                    "WHERE livemode = TRUE AND created_at >= @since GROUP BY status, connector;", new { since })).ToList();
                var ts = rows.Where(r => r.Status == PaymentStatuses.Succeeded).Sum(r => r.Count);
                var ta = rows.Where(r => r.Status == PaymentStatuses.Succeeded).Sum(r => r.Amount);
                var tf = rows.Where(r => r.Status is PaymentStatuses.Failed or PaymentStatuses.Expired or PaymentStatuses.Cancelled).Sum(r => r.Count);
                var tu = await c.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM attempts a JOIN payments p ON p.id = a.payment_id WHERE a.connector = 'upi' AND a.status = 'processing' AND p.livemode = TRUE;");
                succeeded += ts; amount += ta; failed += tf; upi += tu;
                open += rows.Where(r => PaymentStatuses.Open.Contains(r.Status)).Sum(r => r.Count);
                refunded += rows.Sum(r => r.Refunded);
                foreach (var r in rows.Where(r => r.Status == PaymentStatuses.Succeeded && r.Connector is not null))
                {
                    var (n, a) = byConnector.GetValueOrDefault(r.Connector!);
                    byConnector[r.Connector!] = (n + r.Count, a + r.Amount);
                }
                tenants.Add(new TenantVolume(t, ts, ta, tf, tu));
            }
            catch (Exception ex) { logger.LogWarning(ex, "Dashboard: tenant {Tenant} skipped", t.Slug); }
        }
        return _cached = new Dashboard(succeeded, amount, failed, open, upi, refunded,
            tenants.OrderByDescending(t => t.Amount).Take(10).ToList(), byConnector, DateTime.UtcNow);
    }
}
