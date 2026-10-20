using PayLink.Platform;
using PayLink.Tenancy;

namespace PayLink.Infrastructure;

public sealed class HealthState
{
    /// <summary>Tenants whose database couldn't be migrated at startup. The rest keep working.</summary>
    public HashSet<string> DegradedTenants { get; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>On start, brings the platform database and every tenant database up to the latest db/ scripts.</summary>
public sealed class StartupMigrations(IServiceProvider services, HealthState health, ILogger<StartupMigrations> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        using var scope = services.CreateScope();
        var bootstrap = scope.ServiceProvider.GetRequiredService<BootstrapStore>();
        var code = await bootstrap.GetOrCreateSetupCodeAsync();
        if (!await bootstrap.IsSetupCompleteAsync())
        {
            if (bootstrap.SetupCodeIsConfigured)
                logger.LogWarning("PayLink isn't set up yet. Open /setup and enter the setup code from PayLink__SetupCode.");
            else
                logger.LogWarning("PayLink isn't set up yet. Open /setup and enter this setup code: {SetupCode} (also saved in {File})",
                    code, scope.ServiceProvider.GetRequiredService<AppPaths>().SetupCodeFile);
            if (await bootstrap.GetPlatformDatabaseAsync() is null) return;
        }

        var platformDb = scope.ServiceProvider.GetRequiredService<PlatformDb>();
        await platformDb.MigrateAsync();

        if (!await bootstrap.IsSetupCompleteAsync()) return;
        var repo = scope.ServiceProvider.GetRequiredService<PlatformRepository>();
        var tenantDb = scope.ServiceProvider.GetRequiredService<TenantDb>();
        foreach (var tenant in await repo.TenantsAsync(take: int.MaxValue))
        {
            try
            {
                await tenantDb.MigrateAsync(tenantDb.Target(tenant), tenant.Slug);
            }
            catch (Exception ex)
            {
                // One tenant failing to migrate must not stop the others.
                health.DegradedTenants.Add(tenant.Slug);
                logger.LogError(ex, "Tenant {Slug} could not be migrated and is marked degraded", tenant.Slug);
            }
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
