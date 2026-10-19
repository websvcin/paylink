using Microsoft.AspNetCore.Mvc;
using PayLink.Identity;
using PayLink.Infrastructure;
using PayLink.Platform;
using WebSvc.PayLink.Storage.Provisioning;

namespace PayLink.Pages.Admin;

/// <summary>Who may have a MySQL database created for them at self-service signup.</summary>
public sealed class AutoProvisioningModel(PlatformRepository repo, PlatformSettings settings, PlatformDb platformDb, BootstrapStore bootstrap) : AdminPage
{
    public string Mode { get; private set; } = AutoProvisionModes.Approved;
    public IReadOnlyList<AutoProvisionApproval> Approvals { get; private set; } = [];
    public bool HostingConfigured { get; private set; }

    public async Task OnGetAsync()
    {
        Mode = await settings.GetAutoProvisionModeAsync();
        Approvals = await repo.ApprovalsAsync();
        HostingConfigured = (await bootstrap.GetHostingAsync()).Kind != HostingKind.None;
    }

    public async Task<IActionResult> OnPostModeAsync(string mode)
    {
        if (mode is not (AutoProvisionModes.Off or AutoProvisionModes.Approved or AutoProvisionModes.Everyone)) return Failed("Unknown mode.");
        await settings.SetAsync(PlatformSettings.KeyAutoProvisionMode, mode);
        await platformDb.AuditAsync(Me, "autoprov.mode_changed", mode);
        return Done("Saved.");
    }

    public async Task<IActionResult> OnPostAddAsync(string orgSlug, string? note)
    {
        orgSlug = (orgSlug ?? "").Trim().ToLowerInvariant();
        if (!System.Text.RegularExpressions.Regex.IsMatch(orgSlug, "^[a-z0-9][a-z0-9-]{0,98}[a-z0-9]$|^[a-z0-9]$"))
            return Failed("Enter a LoginLink organization slug: lowercase letters, digits and hyphens.");
        if (!await repo.AddApprovalAsync(orgSlug, note?.Trim(), User.Get(PayLinkClaims.Email))) return Failed($"{orgSlug} is already approved.");
        await platformDb.AuditAsync(Me, "autoprov.approved", orgSlug, note);
        return Done($"{orgSlug} can now have a MySQL database created at signup.");
    }

    public async Task<IActionResult> OnPostRemoveAsync(string orgSlug)
    {
        await repo.RemoveApprovalAsync(orgSlug);
        await platformDb.AuditAsync(Me, "autoprov.removed", orgSlug);
        return Done($"{orgSlug} removed. Databases already created aren't affected.");
    }
}
