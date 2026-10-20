using PayLink.Infrastructure;
using PayLink.Platform;
using WebSvc.PayLink.Storage;
using WebSvc.PayLink.Storage.Provisioning;

namespace PayLink.Tenancy;

public sealed record MoveResult(bool Ok, string Message);

/// <summary>
/// Moves a tenant's database, or the platform database, to another engine or server: SQLite, an auto-provisioned MySQL
/// database, or one you run. The new database gets the schema, every row is copied and counted, then PayLink switches to
/// it. The old database is left as it was (delete it yourself once you're happy), so a move can always be undone by hand.
/// A tenant is paused while its data is copied (payers and its team see "temporarily unavailable" for that time).
/// </summary>
public sealed class DatabaseMoves(
    PlatformRepository repo,
    PlatformDb platformDb,
    TenantDb tenantDb,
    BootstrapStore bootstrap,
    SecretProtector secrets,
    AppPaths paths,
    IHttpClientFactory httpFactory,
    ILogger<DatabaseMoves> logger)
{
    public const string MovingStatus = "moving";

    public async Task<MoveResult> MoveTenantAsync(TenantRecord tenant, string to, DatabaseTarget? ownTarget, Actor actor, CancellationToken ct = default)
    {
        if (!tenant.IsActive) return new(false, "Only an active tenant can be moved.");
        var source = tenantDb.Target(tenant);
        var (target, provisioner, provisioned, problem) = await PrepareAsync(to, ownTarget, "tenant_" + tenant.Slug, tenant.Slug, ct);
        if (problem is not null) return new(false, problem);
        if (Same(source, target!)) return new(false, "That's the database the tenant already uses.");

        await repo.SetTenantStatusAsync(tenant.Id, MovingStatus);
        try
        {
            await tenantDb.MigrateAsync(target!, tenant.Slug);
            var copied = await DatabaseCopier.CopyAsync(source, target!, ct: ct);
            var providerRef = provisioned is null ? null : secrets.Protect($"{provisioner!.Kind}|{provisioned.ProviderReference}|{provisioned.DatabaseName}|{provisioned.UserName}");
            await repo.SetTenantDatabaseAsync(tenant.Id, to, target!.Engine.ToString(), target.Describe(), secrets.ProtectJson(paths.Portable(target)), providerRef);
            tenantDb.ClearCache(tenant.Id);
            await repo.SetTenantStatusAsync(tenant.Id, "active");
            var summary = $"{source.Describe()} → {target.Describe()}; {copied.Rows.Values.Sum()} rows in {copied.Rows.Count} tables. The old database was kept.";
            await platformDb.AuditAsync(actor, "tenant.database_moved", tenant.Slug, summary);
            var moved = (await repo.TenantByIdAsync(tenant.Id))!;
            await tenantDb.AuditAsync(moved, actor, "tenant.database_moved", tenant.Slug, summary);
            logger.LogWarning("Tenant {Slug} moved: {Summary}", tenant.Slug, summary);
            return new(true, $"Moved to {target.Describe()}: {copied.Rows.Values.Sum()} rows copied and counted. The old database ({source.Describe()}) is untouched; remove it when you no longer need it.");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Moving tenant {Slug} failed", tenant.Slug);
            await repo.SetTenantStatusAsync(tenant.Id, "active");
            await DiscardAsync(to, target!, provisioner, provisioned);
            return new(false, "The move failed and nothing changed: " + ex.Message);
        }
    }

    public async Task<MoveResult> MovePlatformAsync(string to, DatabaseTarget? ownTarget, Actor actor, CancellationToken ct = default)
    {
        var source = await platformDb.TargetAsync();
        var (target, provisioner, provisioned, problem) = await PrepareAsync(to, ownTarget, "platform", "platform", ct, platform: true);
        if (problem is not null) return new(false, problem);
        if (Same(source, target!)) return new(false, "That's the database the platform already uses.");
        try
        {
            await platformDb.MigrateAsync(target!);
            await platformDb.AuditAsync(actor, "platform.database_moving", null, $"{source.Describe()} → {target!.Describe()}");
            var copied = await DatabaseCopier.CopyAsync(source, target!, ct: ct);
            await bootstrap.SetPlatformDatabaseAsync(target!);
            platformDb.Reset();
            await platformDb.AuditAsync(actor, "platform.database_moved", null, $"{source.Describe()} → {target!.Describe()}; {copied.Rows.Values.Sum()} rows. The old database was kept.");
            return new(true, $"The platform database is now {target.Describe()} ({copied.Rows.Values.Sum()} rows copied and counted). The old one ({source.Describe()}) is untouched.");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Moving the platform database failed");
            await DiscardAsync(to, target!, provisioner, provisioned);
            return new(false, "The move failed and nothing changed: " + ex.Message);
        }
    }

    private async Task<(DatabaseTarget? Target, IDatabaseProvisioner? Provisioner, ProvisionedDatabase? Provisioned, string? Problem)> PrepareAsync(
        string to, DatabaseTarget? ownTarget, string fileStem, string label, CancellationToken ct, bool platform = false)
    {
        switch (to)
        {
            case "sqlite":
                var file = platform
                    ? Path.Combine(paths.DataRoot, $"platform_{ProvisioningNames.RandomToken(4)}.db")
                    : Path.Combine(paths.TenantsDirectory, $"{fileStem}_{ProvisioningNames.RandomToken(4)}.db");
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                return (DatabaseTarget.ForSqliteFile(file), null, null, null);
            case "auto":
                var provisioner = DatabaseProvisioners.Create(await bootstrap.GetHostingAsync(), httpFactory.CreateClient("hosting"));
                if (provisioner is null) return (null, null, null, "Set up database hosting first (Admin → Database hosting).");
                try
                {
                    var created = await provisioner.CreateAsync("paylink_", label, ct);
                    return (created.Target, provisioner, created, null);
                }
                catch (ProvisioningException ex) { return (null, null, null, "The hosting couldn't create the database: " + ex.Message); }
            case "byo":
                if (ownTarget is null) return (null, null, null, "Enter the MySQL connection details.");
                var probe = await DatabaseProbe.TestAsync(ownTarget, ct);
                if (!probe.Ok) return (null, null, null, probe.Message);
                if (!probe.IsEmpty) return (null, null, null, $"The database must be empty, but it has {probe.TableCount} table(s).");
                return (ownTarget, null, null, null);
            default:
                return (null, null, null, "Choose where the data should move to.");
        }
    }

    private static bool Same(DatabaseTarget a, DatabaseTarget b) =>
        a.Engine == b.Engine && string.Equals(a.SqliteFilePath ?? a.ConnectionString, b.SqliteFilePath ?? b.ConnectionString, StringComparison.OrdinalIgnoreCase);

    private static async Task DiscardAsync(string to, DatabaseTarget target, IDatabaseProvisioner? provisioner, ProvisionedDatabase? provisioned)
    {
        try
        {
            if (to == "sqlite" && target.SqliteFilePath is { } file)
            {
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                foreach (var f in new[] { file, file + "-wal", file + "-shm" }) if (File.Exists(f)) File.Delete(f);
            }
            else if (to == "auto" && provisioner is not null && provisioned is not null) await provisioner.DropAsync(provisioned);
        }
        catch (Exception) { }
    }
}
