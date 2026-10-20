using Microsoft.AspNetCore.Mvc;
using PayLink.Identity;
using PayLink.Platform;

namespace PayLink.Pages.Admin;

public sealed class LoginLinkModel(PlatformSettings settings, PlatformDb platformDb, LoginLinkClient loginLink) : AdminPage
{
    public LoginLinkSettings? Saved { get; private set; }
    public string PublicBaseUrl { get; private set; } = "";

    public async Task OnGetAsync()
    {
        Saved = await settings.GetLoginLinkAsync();
        PublicBaseUrl = await settings.GetPublicBaseUrlAsync();
    }

    public async Task<IActionResult> OnPostCheckAsync()
    {
        var ll = await settings.GetLoginLinkAsync();
        if (ll is null) return Failed("LoginLink isn't connected.");
        try
        {
            var config = await loginLink.GetDiscoveryAsync(LoginLinkClient.DiscoveryUrl(ll.ServerUrl, ll.PlatformOrg));
            return Done($"LoginLink is reachable. Issuer {config.Issuer}, {config.SigningKeys.Count} signing key(s).");
        }
        catch (LoginLinkException ex) { return Failed(ex.Message); }
    }

    public async Task<IActionResult> OnPostPlatformApiAsync(string? clientId, string? clientSecret, string action)
    {
        var ll = await settings.GetLoginLinkAsync();
        if (ll is null) return Failed("LoginLink isn't connected.");
        if (action == "remove")
        {
            ll.PlatformApiClientId = null;
            ll.PlatformApiClientSecret = null;
            await settings.SetLoginLinkAsync(ll);
            await platformDb.AuditAsync(Me, "loginlink.platform_api_removed", ll.ServerUrl);
            return Done("Removed. Tenants can still connect an existing organization.");
        }
        if (string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(clientSecret)) return Failed("Enter the client ID and secret.");
        try
        {
            var root = await loginLink.GetDiscoveryAsync(LoginLinkClient.DiscoveryUrl(ll.ServerUrl, null));
            await loginLink.ClientCredentialsTokenAsync(root.TokenEndpoint, clientId.Trim(), clientSecret.Trim());
        }
        catch (LoginLinkException ex) { return Failed(ex.Message); }
        ll.PlatformApiClientId = clientId.Trim();
        ll.PlatformApiClientSecret = clientSecret.Trim();
        await settings.SetLoginLinkAsync(ll);
        await platformDb.AuditAsync(Me, "loginlink.platform_api_set", ll.ServerUrl);
        return Done("Saved. Tenants can now create a new LoginLink organization at signup.");
    }
}
