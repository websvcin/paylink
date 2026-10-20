using Microsoft.AspNetCore.Mvc;
using PayLink.Payments;
using WebSvc.PayLink.Abstractions;

namespace PayLink.Pages.Admin;

/// <summary>
/// The connector catalog (gate 1 of 3): install, allow per mode, limit, drain and uninstall connectors; install or update
/// connector packages from nuget.org or a file while PayLink runs, with rollback.
/// </summary>
[RequestSizeLimit(64 * 1024 * 1024)]
[RequestFormLimits(MultipartBodyLengthLimit = 64 * 1024 * 1024)]
public sealed class ConnectorsModel(ConnectorRegistry registry, ConnectorCatalog catalog, ConnectorPackages packages, PlatformPayments platform) : AdminPage
{
    public IReadOnlyList<(IPaymentConnector Connector, CatalogEntry Entry)> Rows { get; private set; } = [];
    public IReadOnlyList<string> AllowedPrefixes => packages.AllowedPrefixes;

    public async Task OnGetAsync()
    {
        var entries = await catalog.EntriesAsync();
        var rows = registry.All.Select(c => (c, entries.GetValueOrDefault(c.Manifest.Id) ?? new CatalogEntry { ConnectorId = c.Manifest.Id })).ToList();
        // Uninstalled packages are no longer in the registry, but stay listed so they can be installed again.
        rows.AddRange(entries.Values.Where(e => rows.All(r => r.c.Manifest.Id != e.ConnectorId)).Select(e => ((IPaymentConnector)null!, e)));
        Rows = rows.Where(r => r.c is not null).ToList();
    }

    public async Task<IActionResult> OnPostAllowAsync(string id, bool test, bool live, string? maxAmount)
    {
        if (registry.Get(id) is not { } c) return Failed("Unknown connector.");
        var e = await catalog.GetAsync(id);
        e.AllowTest = test;
        e.AllowLive = live && !c.Manifest.TestOnly;
        e.MaxAmount = string.IsNullOrWhiteSpace(maxAmount) ? null : Money.ParseMajor(maxAmount) ?? e.MaxAmount;
        await catalog.SaveAsync(e, Me, "connector.allowed", $"test={e.AllowTest} live={e.AllowLive} max={e.MaxAmount}");
        return Done($"{c.Manifest.Name}: saved. Tenants see it only where it's allowed.");
    }

    public async Task<IActionResult> OnPostStateAsync(string id, string state)
    {
        if (registry.Get(id) is not { } c) return Failed("Unknown connector.");
        var e = await catalog.GetAsync(id);
        if (state == CatalogStates.Uninstalled)
        {
            var (attempts, refunds) = await platform.OpenOnConnectorAsync(id);
            if (attempts > 0 || refunds > 0)
                return Failed($"{c.Manifest.Name} still has {attempts} open payment attempt(s) and {refunds} pending refund(s). Drain it first and uninstall when they've finished.");
            if (e.Source == "package") packages.Unload(id);
        }
        else if (state is not (CatalogStates.Installed or CatalogStates.Draining)) return Failed("Unknown state.");
        e.State = state;
        await catalog.SaveAsync(e, Me, "connector." + state);
        return Done(state switch
        {
            CatalogStates.Draining => $"{c.Manifest.Name} is draining: no new payments; open ones, refunds and late webhooks finish.",
            CatalogStates.Uninstalled => $"{c.Manifest.Name} is uninstalled.",
            _ => $"{c.Manifest.Name} is installed.",
        });
    }

    public async Task<IActionResult> OnPostInstallAsync(string packageId, string? version, IFormFile? package)
    {
        PackageInstallResult result;
        if (package is { Length: > 0 })
        {
            await using var s = package.OpenReadStream();
            result = await packages.InstallFromFileAsync(s);
        }
        else if (!string.IsNullOrWhiteSpace(packageId)) result = await packages.InstallFromNuGetAsync(packageId, version);
        else return Failed("Enter a package id from nuget.org, or choose a .nupkg file.");
        if (!result.Ok) return Failed(result.Message);

        var e = await catalog.GetAsync(result.ConnectorId!);
        var pkgId = registry.Get(result.ConnectorId!)!.GetType().Assembly.GetName().Name;
        if (e.Source == "package" && e.PackageVersion != result.Version) e.PreviousVersion = e.PackageVersion;
        e.Source = "package";
        e.PackageId = string.IsNullOrWhiteSpace(packageId) ? pkgId : packageId.Trim();
        e.PackageVersion = result.Version;
        if (e.State == CatalogStates.Uninstalled) e.State = CatalogStates.Installed;
        await catalog.SaveAsync(e, Me, "connector.package_installed", $"{e.PackageId} {e.PackageVersion}");
        return Done(result.Message);
    }

    public async Task<IActionResult> OnPostRollbackAsync(string id)
    {
        var e = await catalog.GetAsync(id);
        if (e.Source != "package" || e.PreviousVersion is null || e.PackageId is null) return Failed("There's no previous version to go back to.");
        var folder = Path.Combine(packages.Root, e.PackageId.ToLowerInvariant(), e.PreviousVersion.ToLowerInvariant());
        if (!Directory.Exists(folder) || packages.Load(e.PackageId, folder, out var error) is null) return Failed("The previous version can't be loaded.");
        (e.PackageVersion, e.PreviousVersion) = (e.PreviousVersion, e.PackageVersion);
        await catalog.SaveAsync(e, Me, "connector.rolled_back", $"{e.PackageId} {e.PackageVersion}");
        return Done($"Back on {e.PackageId} {e.PackageVersion}.");
    }

    public async Task<IActionResult> OnPostBuiltInAsync(string id)
    {
        var e = await catalog.GetAsync(id);
        if (e.Source != "package" || !registry.IsBuiltIn(id)) return Failed("This connector has no built-in version.");
        packages.Unload(id);
        e.Source = "builtin";
        e.PreviousVersion = e.PackageVersion;
        await catalog.SaveAsync(e, Me, "connector.builtin_restored", id);
        return Done($"Using the {registry.Get(id)!.Manifest.Name} that ships with PayLink again.");
    }
}
