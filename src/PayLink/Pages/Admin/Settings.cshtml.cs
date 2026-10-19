using Microsoft.AspNetCore.Mvc;
using PayLink.Platform;

namespace PayLink.Pages.Admin;

public sealed class SettingsModel(PlatformSettings settings, PlatformDb platformDb) : AdminPage
{
    public bool SignupOpen { get; private set; }
    public string PublicBaseUrl { get; private set; } = "";

    public async Task OnGetAsync()
    {
        SignupOpen = await settings.IsSignupOpenAsync();
        PublicBaseUrl = await settings.GetPublicBaseUrlAsync();
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
