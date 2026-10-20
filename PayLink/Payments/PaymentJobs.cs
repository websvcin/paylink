using PayLink.Infrastructure;
using PayLink.Platform;

namespace PayLink.Payments;

/// <summary>
/// Background work for every active tenant: expire unpaid payments, ask gateways about open attempts and pending refunds,
/// and send due webhooks. Runs every few seconds;
/// one tenant failing never stops the others. PayLink:Jobs:IntervalSeconds sets the pace (default 10; 0 turns the loop off).
/// </summary>
public sealed class PaymentJobs(IServiceProvider services, BootstrapStore bootstrap, IConfiguration configuration, ILogger<PaymentJobs> logger) : BackgroundService
{
    private TimeSpan Interval => TimeSpan.FromSeconds(configuration.GetValue("PayLink:Jobs:IntervalSeconds", 10));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (Interval <= TimeSpan.Zero) return;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (await bootstrap.IsSetupCompleteAsync()) await RunOnceAsync(stoppingToken);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogError(ex, "Payment jobs failed");
            }
            try { await Task.Delay(Interval, stoppingToken); } catch (OperationCanceledException) { }
        }
    }

    public async Task RunOnceAsync(CancellationToken ct = default)
    {
        using var scope = services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<PlatformRepository>();
        var payments = scope.ServiceProvider.GetRequiredService<PaymentService>();
        var webhooks = scope.ServiceProvider.GetRequiredService<WebhookService>();
        var health = scope.ServiceProvider.GetRequiredService<HealthState>();
        foreach (var tenant in await repo.TenantsAsync(take: int.MaxValue))
        {
            if (!tenant.IsActive || health.DegradedTenants.Contains(tenant.Slug) || ct.IsCancellationRequested) continue;
            try
            {
                await payments.ExpireDueAsync(tenant);
                await payments.PollAsync(tenant, ct);
                await payments.PollRefundsAsync(tenant, ct);
                await webhooks.DeliverDueAsync(tenant, ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                logger.LogError(ex, "Payment jobs failed for tenant {Tenant}", tenant.Slug);
            }
        }
    }
}
