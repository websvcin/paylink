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
    private readonly AppPaths _paths;
    private readonly string? _configuredSetupCode;
    private readonly object _initLock = new();
    private bool _initialized;

    public BootstrapStore(AppPaths paths, SecretProtector secrets, IConfiguration configuration)
    {
        _target = DatabaseTarget.ForSqliteFile(paths.BootstrapFile);
        _secrets = secrets;
        _paths = paths;
        var code = configuration["PayLink:SetupCode"];
        _configuredSetupCode = string.IsNullOrWhiteSpace(code) ? null : code.Trim().ToUpperInvariant();
    }

    private const string SetupComplete = "setup_complete";
    private const string SetupCode = "setup_code";
    private const string PlatformDb = "platform_db";
    private const string Hosting = "hosting";
    private const string HostingDraft = "hosting_draft";
    private const string InitialTenantAutoProvision = "autoprov_mode_initial";

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

    public async Task MarkSetupCompleteAsync()
    {
        await Set(SetupComplete, "true");
        await Set(HostingDraft, null);
        try { File.Delete(_paths.SetupCodeFile); } catch (IOException) { }
    }

    /// <summary>
    /// A one-time code whoever runs setup must know, so a fresh install exposed to the internet can't be claimed by a stranger.
    /// It's printed in the startup log and written to App_Data/setup-code.txt; PayLink__SetupCode sets it instead.
    /// </summary>
    public async Task<string> GetOrCreateSetupCodeAsync()
    {
        var code = _configuredSetupCode ?? await Get(SetupCode);
        if (code is null)
        {
            code = ProvisioningNames.RandomToken(10).ToUpperInvariant();
            await Set(SetupCode, code);
        }
        if (_configuredSetupCode is null && !await IsSetupCompleteAsync() && !File.Exists(_paths.SetupCodeFile))
        {
            try { await File.WriteAllTextAsync(_paths.SetupCodeFile, code + Environment.NewLine); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return code;
    }

    public bool SetupCodeIsConfigured => _configuredSetupCode is not null;

    public async Task<DatabaseTarget?> GetPlatformDatabaseAsync() =>
        _secrets.UnprotectJson<DatabaseTarget>(await Get(PlatformDb)) is { } t ? _paths.Resolve(t) : null;
    public Task SetPlatformDatabaseAsync(DatabaseTarget target) => Set(PlatformDb, _secrets.ProtectJson(_paths.Portable(target)));

    public async Task<HostingSettings> GetHostingAsync() => _secrets.UnprotectJson<HostingSettings>(await Get(Hosting)) ?? new HostingSettings();

    public async Task SetHostingAsync(HostingSettings settings)
    {
        await Set(Hosting, _secrets.ProtectJson(settings));
        await Set(HostingDraft, null);
    }

    /// <summary>
    /// The last settings someone tested but hasn't saved yet. Password fields come back empty after a test,
    /// so saving falls back to these secrets instead of storing blanks.
    /// </summary>
    public async Task<HostingSettings?> GetHostingDraftAsync() => _secrets.UnprotectJson<HostingSettings>(await Get(HostingDraft));
    public Task SetHostingDraftAsync(HostingSettings? settings) => Set(HostingDraft, settings is null ? null : _secrets.ProtectJson(settings));

    /// <summary>The tenant auto-provisioning rule chosen during setup, before the platform database exists to hold it.</summary>
    public Task<string?> GetInitialTenantAutoProvisionAsync() => Get(InitialTenantAutoProvision);
    public Task SetInitialTenantAutoProvisionAsync(string mode) => Set(InitialTenantAutoProvision, mode);
}
