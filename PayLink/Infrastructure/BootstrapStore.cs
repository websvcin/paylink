using Dapper;
using WebSvc.PayLink.Storage;
using WebSvc.PayLink.Storage.Provisioning;

namespace PayLink.Infrastructure;

/// <summary>
/// bootstrap.db: always a small local SQLite file, because it is what tells PayLink where the platform database is.
/// Holds setup progress, the encrypted platform database connection and the encrypted database-hosting settings.
/// </summary>
public sealed class BootstrapStore
{
    private readonly DatabaseTarget _target;
    private readonly SecretProtector _secrets;
    private readonly object _initLock = new();
    private bool _initialized;

    public BootstrapStore(AppPaths paths, SecretProtector secrets)
    {
        _target = DatabaseTarget.ForSqliteFile(paths.BootstrapFile);
        _secrets = secrets;
    }

    private const string SetupComplete = "setup_complete";
    private const string SetupCode = "setup_code";
    private const string PlatformDb = "platform_db";
    private const string Hosting = "hosting";

    private void EnsureCreated()
    {
        if (_initialized) return;
        lock (_initLock)
        {
            if (_initialized) return;
            using var c = Db.OpenAsync(_target).GetAwaiter().GetResult();
            c.Execute("CREATE TABLE IF NOT EXISTS bootstrap (k VARCHAR(100) PRIMARY KEY, v TEXT NULL, updated_at VARCHAR(40) NOT NULL);");
            _initialized = true;
        }
    }

    public Task<string?> GetValueAsync(string key) => Get(key);
    public Task SetValueAsync(string key, string? value) => Set(key, value);

    private async Task<string?> Get(string key)
    {
        EnsureCreated();
        await using var c = await Db.OpenAsync(_target);
        return await c.ExecuteScalarAsync<string?>("SELECT v FROM bootstrap WHERE k = @key;", new { key });
    }

    private async Task Set(string key, string? value)
    {
        EnsureCreated();
        await using var c = await Db.OpenAsync(_target);
        await c.ExecuteAsync(SqlDialect.Sqlite.Upsert("bootstrap", "k", "v", "updated_at"), new { k = key, v = value, updated_at = Clock.Now() });
    }

    public async Task<bool> IsSetupCompleteAsync() => await Get(SetupComplete) == "true";
    public Task MarkSetupCompleteAsync() => Set(SetupComplete, "true");

    /// <summary>A one-time code printed in the startup log; whoever runs setup must know it, so a fresh install exposed to the internet can't be claimed by a stranger.</summary>
    public async Task<string> GetOrCreateSetupCodeAsync()
    {
        var code = await Get(SetupCode);
        if (code is not null) return code;
        code = ProvisioningNames.RandomToken(10).ToUpperInvariant();
        await Set(SetupCode, code);
        return code;
    }

    public async Task<DatabaseTarget?> GetPlatformDatabaseAsync() => _secrets.UnprotectJson<DatabaseTarget>(await Get(PlatformDb));
    public Task SetPlatformDatabaseAsync(DatabaseTarget target) => Set(PlatformDb, _secrets.ProtectJson(target));

    public async Task<HostingSettings> GetHostingAsync() => _secrets.UnprotectJson<HostingSettings>(await Get(Hosting)) ?? new HostingSettings();
    public Task SetHostingAsync(HostingSettings settings) => Set(Hosting, _secrets.ProtectJson(settings));
}
