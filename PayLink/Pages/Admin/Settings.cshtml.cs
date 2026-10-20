using Microsoft.AspNetCore.Mvc;
using PayLink.Infrastructure;
using PayLink.Pages;
using PayLink.Payments;
using PayLink.Platform;

namespace PayLink.Pages.Admin;

public sealed class SettingsModel(PlatformSettings settings, PlatformDb platformDb, AppPaths paths, PayLink.Payments.ConnectorRegistry connectors,
    BackupService backups, SecretProtector secrets) : AdminPage
{
    public string KeySource => secrets.Source;
    public string KeyFingerprint => secrets.Fingerprint;

    /// <summary>A .zip of the data folder (consistent SQLite copies and uploaded files). Admins only; every download is audited.</summary>
    public async Task<IActionResult> OnGetBackupAsync()
    {
        if (!CanChange) return Forbid();
        var temp = Path.GetTempFileName();
        await using (var f = System.IO.File.Create(temp)) await backups.WriteBackupAsync(f);
        await platformDb.AuditAsync(Me, "backup.downloaded", null);
        var stream = new FileStream(temp, FileMode.Open, FileAccess.Read, FileShare.None, 81920, FileOptions.DeleteOnClose);
        return File(stream, "application/zip", $"paylink-backup-{DateTime.UtcNow:yyyyMMdd-HHmm}.zip");
    }

    public IReadOnlyList<WebSvc.PayLink.Abstractions.IPaymentConnector> Connectors => connectors.All;
    public string DataRoot => paths.DataRoot;
    public IReadOnlyList<DataFolderItem> DataFolderItems => DataFolder.Describe(paths);
    public bool SignupOpen { get; private set; }
    public string PublicBaseUrl { get; private set; } = "";
    public IReadOnlySet<string> TenantDbOptionsAllowed { get; private set; } = new HashSet<string>();

    public string LimitMax { get; private set; } = "";
    public string LimitCurrencies { get; private set; } = "";
    public string TenantDomain { get; private set; } = "";
    public string PlatformDescription { get; private set; } = "";

    public async Task<IActionResult> OnPostDomainAsync(string? tenantDomain)
    {
        var d = (tenantDomain ?? "").Trim().TrimStart('*').TrimStart('.').TrimEnd('.').ToLowerInvariant();
        if (d.Length > 0 && (Uri.CheckHostName(d) != UriHostNameType.Dns || !d.Contains('.'))) return Failed("Enter a domain like paylink.websvc.in (no https://).");
        await settings.SetAsync(PlatformSettings.KeyTenantDomain, d.Length == 0 ? null : d);
        PayLink.Tenancy.TenantHosts.Forget();
        await platformDb.AuditAsync(Me, "settings.tenant_domain_changed", d);
        return Done(d.Length == 0 ? "Subdomains are off." : $"Saved. Switch tenants to their own subdomain (slug.{d}) on their page in Tenants.");
    }

    public async Task<IActionResult> OnPostMovePlatformAsync(string to, [FromForm(Name = "Platform")] MySqlInput? platform)
    {
        WebSvc.PayLink.Storage.DatabaseTarget? own = null;
        if (to == "byo" && (platform is null || !platform.TryBuild(out own, out var error))) return Failed(platform is null ? "Enter the MySQL details." : "Check the MySQL details.");
        var moves = HttpContext.RequestServices.GetRequiredService<PayLink.Tenancy.DatabaseMoves>();
        var result = await moves.MovePlatformAsync(to, own, Me, HttpContext.RequestAborted);
        return result.Ok ? Done(result.Message) : Failed(result.Message);
    }

    public async Task<IActionResult> OnPostLimitsAsync(string? maxAmount, string? currencies)
    {
        long? max = null;
        if (!string.IsNullOrWhiteSpace(maxAmount))
        {
            if (Money.ParseMajor(maxAmount) is not { } m) return Failed("Enter the largest payment in rupees, e.g. 200000, or leave it blank.");
            max = m;
        }
        var list = (currencies ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(c => c.ToUpperInvariant()).ToList();
        if (list.Any(c => c.Length != 3 || !c.All(char.IsLetter))) return Failed("Currencies are 3-letter codes separated by commas, e.g. INR, USD.");
        await settings.SetAsync(PlatformSettings.KeyLimitMaxAmount, max?.ToString());
        await settings.SetAsync(PlatformSettings.KeyLimitCurrencies, list.Count == 0 ? null : string.Join(",", list));
        await platformDb.AuditAsync(Me, "settings.limits_changed", $"max={max} currencies={string.Join(",", list)}");
        return Done("Saved. New payments above the limit or in other currencies are refused for every tenant.");
    }

    public async Task OnGetAsync()
    {
        var (max, cur) = await settings.GetLimitsAsync();
        LimitMax = max is { } m ? (m / 100m).ToString("0.##") : "";
        LimitCurrencies = cur is null ? "" : string.Join(", ", cur);
        TenantDomain = await settings.GetTenantDomainAsync();
        PlatformDescription = (await platformDb.TargetAsync()).Describe();
        SignupOpen = await settings.IsSignupOpenAsync();
        PublicBaseUrl = await settings.GetPublicBaseUrlAsync();
        TenantDbOptionsAllowed = await settings.GetTenantDbOptionsAsync();
    }

    public async Task<IActionResult> OnPostDbOptionsAsync(List<string> options)
    {
        var chosen = options.Intersect(TenantDbOptions.All.Select(o => o.Key)).ToList();
        if (chosen.Count == 0) return Failed("Leave at least one option, or close signup instead.");
        await settings.SetTenantDbOptionsAsync(chosen);
        await platformDb.AuditAsync(Me, "settings.tenant_db_options_changed", string.Join(",", chosen));
        return Done("Saved. New signups see only these options.");
    }

    public async Task<IActionResult> OnPostAsync(string signup, string? publicBaseUrl)
    {
        await settings.SetAsync(PlatformSettings.KeySignupMode, signup == "open" ? "open" : "off");
        if (!string.IsNullOrWhiteSpace(publicBaseUrl))
        {
            if (!Uri.TryCreate(publicBaseUrl.Trim(), UriKind.Absolute, out _)) return Failed("The public address must be a full URL, e.g. https://paylink.websvc.in");
            await settings.SetAsync(PlatformSettings.KeyPublicBaseUrl, publicBaseUrl.Trim().TrimEnd('/'));
        }
        await platformDb.AuditAsync(Me, "settings.changed", $"signup={signup}", publicBaseUrl);
        return Done("Saved.");
    }
}
