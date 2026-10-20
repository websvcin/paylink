using System.Data.Common;
using Dapper;
using PayLink.Infrastructure;
using WebSvc.PayLink.Storage;

namespace PayLink.Platform;

/// <summary>Access to the platform database, wherever setup put it (SQLite or MySQL).</summary>
public sealed class PlatformDb(BootstrapStore bootstrap, AppPaths paths, ILogger<PlatformDb> logger)
{
    private DatabaseTarget? _target;

    public async Task<DatabaseTarget> TargetAsync()
    {
        _target ??= await bootstrap.GetPlatformDatabaseAsync()
            ?? throw new InvalidOperationException("The platform database hasn't been chosen yet. Finish /setup first.");
        return _target;
    }

    public void Reset() => _target = null;

    public async Task<DbConnection> OpenAsync() => await Db.OpenAsync(await TargetAsync());

    public async Task<SqlDialect> DialectAsync() => Db.Dialect((await TargetAsync()).Engine);

    public static string EngineFolder(DbEngine engine) => engine == DbEngine.Sqlite ? "sqlite" : "mysql";

    /// <summary>Applies any new scripts from db/{engine}/platform.</summary>
    public async Task<MigrationResult> MigrateAsync(DatabaseTarget? target = null)
    {
        target ??= await TargetAsync();
        var scripts = SqlMigrator.LoadFromDirectory(paths.Scripts(EngineFolder(target.Engine), "platform"));
        var result = await SqlMigrator.ApplyAsync(target, scripts);
        if (result.Applied.Count > 0)
            logger.LogInformation("Platform database: applied migrations {Versions}", string.Join(", ", result.Applied));
        return result;
    }

    public async Task AuditAsync(Actor actor, string action, string? target, string? details = null)
    {
        await using var c = await OpenAsync();
        await AuditLog.WriteAsync(c, actor, action, target, details);
    }
}
