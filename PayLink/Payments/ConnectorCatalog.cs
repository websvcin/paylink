using Dapper;
using PayLink.Infrastructure;
using PayLink.Platform;
using WebSvc.PayLink.Abstractions;

namespace PayLink.Payments;

public sealed class CatalogEntry
{
    public string ConnectorId { get; set; } = "";
    /// <summary>installed | draining | uninstalled</summary>
    public string State { get; set; } = CatalogStates.Installed;
    public bool AllowTest { get; set; } = true;
    public bool AllowLive { get; set; } = true;
    public long? MaxAmount { get; set; }
    /// <summary>builtin (shipped with PayLink) | package (installed at runtime from nuget.org or a file)</summary>
    public string Source { get; set; } = "builtin";
    public string? PackageId { get; set; }
    public string? PackageVersion { get; set; }
    public string? PreviousVersion { get; set; }
    public string UpdatedAt { get; set; } = "";
    public string? UpdatedBy { get; set; }

    public bool Allows(bool livemode) => State == CatalogStates.Installed && (livemode ? AllowLive : AllowTest);
}

public static class CatalogStates
{
    public const string Installed = "installed";
    /// <summary>No new payments; open payments, refunds and late webhooks finish.</summary>
    public const string Draining = "draining";
    public const string Uninstalled = "uninstalled";
}

/// <summary>
/// Gate 1 of 3: what the platform installed and allows. A connector the platform hasn't allowed for a mode is never
/// offered in that mode, whatever a tenant turned on; each layer can only narrow the one above it.
/// Connectors without a row are the built-in defaults: installed, allowed in Test and Live.
/// </summary>
public sealed class ConnectorCatalog(PlatformDb platformDb, BootstrapStore bootstrap)
{
    private Dictionary<string, CatalogEntry>? _cache;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public async Task<IReadOnlyDictionary<string, CatalogEntry>> EntriesAsync()
    {
        if (_cache is { } cached) return cached;
        if (!await bootstrap.IsSetupCompleteAsync() && await bootstrap.GetPlatformDatabaseAsync() is null) return new Dictionary<string, CatalogEntry>();
        await _lock.WaitAsync();
        try
        {
            await using var c = await platformDb.OpenAsync();
            _cache = (await c.QueryAsync<CatalogEntry>("SELECT * FROM connector_catalog;")).ToDictionary(e => e.ConnectorId);
            return _cache;
        }
        finally { _lock.Release(); }
    }

    public async Task<CatalogEntry> GetAsync(string connectorId) =>
        (await EntriesAsync()).GetValueOrDefault(connectorId) ?? new CatalogEntry { ConnectorId = connectorId };

    /// <summary>Whether a payment of this mode and amount may be offered this connector.</summary>
    public async Task<bool> OffersAsync(string connectorId, bool livemode, long amount)
    {
        var e = await GetAsync(connectorId);
        return e.Allows(livemode) && (e.MaxAmount is null || amount <= e.MaxAmount);
    }

    public async Task SaveAsync(CatalogEntry entry, Actor actor, string action, string? details = null)
    {
        entry.UpdatedAt = Clock.Now();
        entry.UpdatedBy = actor.Name;
        await using var c = await platformDb.OpenAsync();
        var dialect = await platformDb.DialectAsync();
        await c.ExecuteAsync(dialect.Upsert("connector_catalog", "connector_id", "state", "allow_test", "allow_live", "max_amount", "source",
            "package_id", "package_version", "previous_version", "updated_at", "updated_by"), new
        {
            connector_id = entry.ConnectorId, state = entry.State, allow_test = entry.AllowTest, allow_live = entry.AllowLive, max_amount = entry.MaxAmount,
            source = entry.Source, package_id = entry.PackageId, package_version = entry.PackageVersion, previous_version = entry.PreviousVersion,
            updated_at = entry.UpdatedAt, updated_by = entry.UpdatedBy,
        });
        await AuditLog.WriteAsync(c, actor, action, entry.ConnectorId, details);
        _cache = null;
    }

    public void Invalidate() => _cache = null;
}
