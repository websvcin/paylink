using Microsoft.AspNetCore.Mvc;
using PayLink.Infrastructure;
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

    public async Task OnGetAsync()
    {
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
