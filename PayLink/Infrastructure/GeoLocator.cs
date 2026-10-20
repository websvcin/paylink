using System.Net;
using MaxMind.Db;

namespace PayLink.Infrastructure;

/// <summary>Where an IP address is, as far as the location data can tell. Unknown is null, and rules that need it fail open.</summary>
public interface IGeoLocator
{
    string? Country(string? ip);
    bool IsTor(string? ip);
}

/// <summary>
/// Optional location data in the data folder: geo/country.mmdb (a MaxMind GeoLite2 Country or DB-IP Country Lite file,
/// uploaded in Admin → Location data) and geo/tor-exits.txt (Tor's public exit list, refreshed from that page).
/// PayLink ships neither file: their licences ask you to download them yourself.
/// </summary>
public sealed class GeoLocator(AppPaths paths, ILogger<GeoLocator> logger) : IGeoLocator, IDisposable
{
    public const string TorListUrl = "https://check.torproject.org/torbulkexitlist";
    private Reader? _reader;
    private HashSet<string> _tor = [];
    private bool _loaded;
    private readonly object _lock = new();

    public string Folder => Path.Combine(paths.DataRoot, "geo");
    public string CountryFile => Path.Combine(Folder, "country.mmdb");
    public string TorFile => Path.Combine(Folder, "tor-exits.txt");

    public (bool Countries, string? Database, DateTime? Built, int TorExits, DateTime? TorUpdated) Status()
    {
        EnsureLoaded();
        return (_reader is not null, _reader?.Metadata.DatabaseType, _reader?.Metadata.BuildDate, _tor.Count,
            File.Exists(TorFile) ? File.GetLastWriteTimeUtc(TorFile) : null);
    }

    public void Reload()
    {
        lock (_lock) { _reader?.Dispose(); _reader = null; _loaded = false; }
        EnsureLoaded();
    }

    private void EnsureLoaded()
    {
        if (_loaded) return;
        lock (_lock)
        {
            if (_loaded) return;
            try { if (File.Exists(CountryFile)) _reader = new Reader(CountryFile, FileAccessMode.Memory); }
            catch (Exception ex) when (ex is InvalidDatabaseException or IOException) { logger.LogWarning(ex, "The location database can't be read"); }
            _tor = File.Exists(TorFile)
                ? File.ReadAllLines(TorFile).Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith('#')).ToHashSet()
                : [];
            _loaded = true;
        }
    }

    public string? Country(string? ip)
    {
        EnsureLoaded();
        if (_reader is null || ip is null || !IPAddress.TryParse(ip, out var address)) return null;
        try
        {
            var data = _reader.Find<Dictionary<string, object>>(address);
            return data?.GetValueOrDefault("country") is Dictionary<string, object> c && c.GetValueOrDefault("iso_code") is string iso ? iso.ToUpperInvariant() : null;
        }
        catch (InvalidDatabaseException) { return null; }
    }

    public bool IsTor(string? ip)
    {
        EnsureLoaded();
        return ip is not null && _tor.Contains(ip);
    }

    /// <summary>Validates and installs an uploaded .mmdb country database.</summary>
    public string? Install(Stream mmdb)
    {
        Directory.CreateDirectory(Folder);
        var temp = CountryFile + ".upload";
        using (var f = File.Create(temp)) mmdb.CopyTo(f);
        try
        {
            using var test = new Reader(temp, FileAccessMode.Memory);
            if (!test.Metadata.DatabaseType.Contains("Country", StringComparison.OrdinalIgnoreCase) && !test.Metadata.DatabaseType.Contains("City", StringComparison.OrdinalIgnoreCase))
                return $"That's a {test.Metadata.DatabaseType} database; upload a Country (or City) database.";
        }
        catch (Exception ex) when (ex is InvalidDatabaseException or IOException)
        {
            File.Delete(temp);
            return "That file isn't a MaxMind DB (.mmdb).";
        }
        lock (_lock) { _reader?.Dispose(); _reader = null; _loaded = false; }
        File.Move(temp, CountryFile, overwrite: true);
        Reload();
        return null;
    }

    public async Task<string> RefreshTorAsync(HttpClient http, CancellationToken ct = default)
    {
        var text = await http.GetStringAsync(TorListUrl, ct);
        var count = text.Split('\n').Count(l => IPAddress.TryParse(l.Trim(), out _));
        if (count == 0) return "The Tor list was empty; kept the previous one.";
        Directory.CreateDirectory(Folder);
        await File.WriteAllTextAsync(TorFile, text, ct);
        Reload();
        return $"{count} Tor exit addresses loaded.";
    }

    public void Dispose() => _reader?.Dispose();
}
