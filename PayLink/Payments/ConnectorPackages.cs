using System.IO.Compression;
using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using PayLink.Infrastructure;
using WebSvc.PayLink.Abstractions;

namespace PayLink.Payments;

public sealed record PackageInstallResult(bool Ok, string Message, string? ConnectorId = null, string? Version = null);

/// <summary>
/// Installs connector packages while PayLink runs: from nuget.org (by id and version) or from an uploaded .nupkg.
/// Each package is unpacked to App_Data/connectors/{id}/{version}/ and loaded into its own collectible
/// AssemblyLoadContext that shares only WebSvc.PayLink.Abstractions and the .NET/ASP.NET framework with PayLink, so one
/// connector's libraries can't clash with another's. Only ids under PayLink:Connectors:AllowedPackagePrefixes
/// (default "WebSvc.PayLink.Connector.") may be installed: a package is code that runs inside PayLink.
/// A failed install or update leaves the previous version running.
/// </summary>
public sealed class ConnectorPackages(AppPaths paths, IHttpClientFactory httpFactory, ConnectorRegistry registry, IConfiguration configuration, ILogger<ConnectorPackages> logger)
{
    public const string HttpClientName = "nuget";
    public static int AbstractionsMajor => typeof(IPaymentConnector).Assembly.GetName().Version?.Major ?? 0;
    private readonly Dictionary<string, ConnectorLoadContext> _contexts = new();

    public string Root => Path.Combine(paths.DataRoot, "connectors");

    public IReadOnlyList<string> AllowedPrefixes =>
        (configuration["PayLink:Connectors:AllowedPackagePrefixes"] ?? "WebSvc.PayLink.Connector.")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    public bool IsAllowed(string packageId) => AllowedPrefixes.Any(p => packageId.StartsWith(p, StringComparison.OrdinalIgnoreCase));

    public async Task<PackageInstallResult> InstallFromNuGetAsync(string packageId, string? version, CancellationToken ct = default)
    {
        packageId = packageId.Trim();
        if (!IsAllowed(packageId)) return new(false, $"Only packages starting with {string.Join(" or ", AllowedPrefixes)} can be installed (PayLink:Connectors:AllowedPackagePrefixes).");
        var http = httpFactory.CreateClient(HttpClientName);
        var lower = packageId.ToLowerInvariant();
        try
        {
            if (string.IsNullOrWhiteSpace(version))
            {
                var index = JsonNode.Parse(await http.GetStringAsync($"https://api.nuget.org/v3-flatcontainer/{lower}/index.json", ct));
                version = (index?["versions"] as JsonArray)?.Select(v => (string?)v).Where(v => v is not null && !v.Contains('-')).LastOrDefault();
                if (version is null) return new(false, $"nuget.org has no released version of {packageId}.");
            }
            version = version.Trim().ToLowerInvariant();
            using var response = await http.GetAsync($"https://api.nuget.org/v3-flatcontainer/{lower}/{version}/{lower}.{version}.nupkg", ct);
            if (!response.IsSuccessStatusCode) return new(false, $"nuget.org answered {(int)response.StatusCode} for {packageId} {version}.");
            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            return await InstallAsync(stream, packageId, ct);
        }
        catch (HttpRequestException ex) { return new(false, "Couldn't reach nuget.org: " + ex.Message); }
    }

    public Task<PackageInstallResult> InstallFromFileAsync(Stream nupkg, CancellationToken ct = default) => InstallAsync(nupkg, null, ct);

    private async Task<PackageInstallResult> InstallAsync(Stream nupkg, string? expectedId, CancellationToken ct)
    {
        var temp = Path.Combine(Path.GetTempPath(), "paylink-pkg-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            var file = Path.Combine(temp, "package.nupkg");
            await using (var f = File.Create(file)) await nupkg.CopyToAsync(f, ct);
            using var zip = ZipFile.OpenRead(file);
            var nuspec = zip.Entries.FirstOrDefault(e => e.FullName.EndsWith(".nuspec", StringComparison.OrdinalIgnoreCase) && !e.FullName.Contains('/'));
            if (nuspec is null) return new(false, "That isn't a NuGet package (no .nuspec).");
            XDocument spec;
            await using (var s = nuspec.Open()) spec = XDocument.Load(s);
            var meta = spec.Root?.Elements().FirstOrDefault(e => e.Name.LocalName == "metadata");
            var id = meta?.Elements().FirstOrDefault(e => e.Name.LocalName == "id")?.Value;
            var version = meta?.Elements().FirstOrDefault(e => e.Name.LocalName == "version")?.Value;
            if (id is null || version is null) return new(false, "The package has no id or version.");
            if (expectedId is not null && !id.Equals(expectedId, StringComparison.OrdinalIgnoreCase)) return new(false, $"The package is {id}, not {expectedId}.");
            if (!IsAllowed(id)) return new(false, $"{id} isn't allowed: only packages starting with {string.Join(" or ", AllowedPrefixes)} can be installed.");
            var others = meta!.Descendants().Where(e => e.Name.LocalName == "dependency").Select(e => e.Attribute("id")?.Value)
                .Where(d => d is not null && !d.Equals("WebSvc.PayLink.Abstractions", StringComparison.OrdinalIgnoreCase)).Distinct().ToList();

            // Prefer the most specific framework the package targets that PayLink can run.
            var lib = new[] { "lib/net8.0/", "lib/net7.0/", "lib/net6.0/", "lib/netstandard2.1/", "lib/netstandard2.0/" }
                .FirstOrDefault(prefix => zip.Entries.Any(e => e.FullName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && e.Name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)));
            if (lib is null) return new(false, $"{id} {version} has no .NET 8 compatible library (lib/net8.0 or netstandard2.x).");

            var target = Path.Combine(Root, id.ToLowerInvariant(), version.ToLowerInvariant());
            if (Directory.Exists(target)) Directory.Delete(target, true);
            Directory.CreateDirectory(target);
            foreach (var e in zip.Entries.Where(e => e.FullName.StartsWith(lib, StringComparison.OrdinalIgnoreCase) && !e.FullName.EndsWith('/')))
            {
                var name = Path.GetFileName(e.FullName);
                if (name.Length == 0 || name.Contains("..")) continue;
                e.ExtractToFile(Path.Combine(target, name), overwrite: true);
            }

            var loaded = Load(id, target, out var error);
            if (loaded is null)
            {
                try { Directory.Delete(target, true); } catch (IOException) { }
                return new(false, error + (others.Count > 0 ? $" It depends on {string.Join(", ", others)}, which must be part of PayLink or bundled in the package." : ""));
            }
            return new(true, $"{id} {version}: {loaded.Manifest.Name} ({loaded.Manifest.Id}) is installed."
                + (others.Count > 0 ? $" Note: it depends on {string.Join(", ", others)}." : ""), loaded.Manifest.Id, version);
        }
        catch (InvalidDataException) { return new(false, "The package file is damaged."); }
        finally
        {
            try { Directory.Delete(temp, true); } catch (IOException) { }
        }
    }

    /// <summary>Loads an unpacked package and registers its connector. Null (with a reason) when it can't be used.</summary>
    public IPaymentConnector? Load(string packageId, string folder, out string error)
    {
        error = "";
        var context = new ConnectorLoadContext(folder);
        try
        {
            IPaymentConnector? connector = null;
            foreach (var dll in Directory.GetFiles(folder, "*.dll"))
            {
                Assembly assembly;
                try { assembly = context.LoadFromAssemblyPath(dll); }
                catch (BadImageFormatException) { continue; }
                foreach (var type in SafeTypes(assembly).Where(t => t is { IsClass: true, IsAbstract: false } && typeof(IPaymentConnector).IsAssignableFrom(t)))
                {
                    var withHttp = type.GetConstructor([typeof(HttpClient)]);
                    connector = withHttp is not null
                        ? (IPaymentConnector)withHttp.Invoke([registry.CreateHttpClient()])
                        : type.GetConstructor(Type.EmptyTypes) is { } plain ? (IPaymentConnector)plain.Invoke(null) : null;
                    if (connector is not null) break;
                }
                if (connector is not null) break;
            }
            if (connector is null)
            {
                error = $"{packageId} has no payment connector PayLink can create (a public class implementing IPaymentConnector with a constructor taking HttpClient, or none).";
                context.Unload();
                return null;
            }
            if (connector.Manifest.AbstractionsMajorVersion != AbstractionsMajor)
            {
                error = $"{packageId} was built for WebSvc.PayLink.Abstractions {connector.Manifest.AbstractionsMajorVersion}.x; this PayLink has {AbstractionsMajor}.x.";
                context.Unload();
                return null;
            }
            lock (_contexts)
            {
                if (_contexts.TryGetValue(connector.Manifest.Id, out var previous)) previous.Unload();
                _contexts[connector.Manifest.Id] = context;
            }
            registry.Install(connector);
            logger.LogInformation("Loaded connector {Connector} {Version} from {Package}", connector.Manifest.Id, connector.Manifest.Version, packageId);
            return connector;
        }
        catch (Exception ex) when (ex is FileNotFoundException or FileLoadException or TargetInvocationException or TypeLoadException or MissingMethodException)
        {
            error = $"{packageId} couldn't be loaded: {(ex.InnerException ?? ex).Message}";
            context.Unload();
            return null;
        }
    }

    /// <summary>Loads the packages the catalog says are installed (at startup).</summary>
    public void LoadInstalled(IEnumerable<CatalogEntry> entries)
    {
        foreach (var e in entries.Where(e => e.Source == "package" && e.State != CatalogStates.Uninstalled && e.PackageId is not null && e.PackageVersion is not null))
        {
            var folder = Path.Combine(Root, e.PackageId!.ToLowerInvariant(), e.PackageVersion!.ToLowerInvariant());
            if (!Directory.Exists(folder) || Load(e.PackageId!, folder, out var error) is null)
                logger.LogError("Connector package {Package} {Version} couldn't be loaded; {Connector} falls back to the built-in version, if any.", e.PackageId, e.PackageVersion, e.ConnectorId);
        }
    }

    public void Unload(string connectorId)
    {
        registry.Remove(connectorId);
        lock (_contexts)
            if (_contexts.Remove(connectorId, out var context)) context.Unload();
    }

    private static IEnumerable<Type> SafeTypes(Assembly assembly)
    {
        try { return assembly.GetExportedTypes(); }
        catch (ReflectionTypeLoadException ex) { return ex.Types.Where(t => t is not null)!; }
    }

    /// <summary>Shares the connector contract and the framework with PayLink; everything else comes from the package's own folder.</summary>
    private sealed class ConnectorLoadContext(string folder) : AssemblyLoadContext(isCollectible: true)
    {
        protected override Assembly? Load(AssemblyName name)
        {
            if (name.Name is null || name.Name == "WebSvc.PayLink.Abstractions" || name.Name.StartsWith("System", StringComparison.Ordinal)
                || name.Name.StartsWith("Microsoft.", StringComparison.Ordinal) || name.Name == "netstandard")
                return null;   // from PayLink (the default context)
            var file = Path.Combine(folder, name.Name + ".dll");
            return File.Exists(file) ? LoadFromAssemblyPath(file) : null;
        }
    }
}
