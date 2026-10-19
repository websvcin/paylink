using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PayLink.Identity;
using PayLink.Platform;

namespace PayLink.Pages.Console;

/// <summary>/{slug}/login: sends the person to sign in through the tenant's own LoginLink organization.</summary>
public sealed class LoginModel(PlatformRepository repo, PlatformSettings settings, OidcSignIn oidc) : PageModel
{
    public TenantRecord? Tenant { get; private set; }
    public string? Error { get; private set; }

    public async Task<IActionResult> OnGetAsync(string slug, bool go = false)
    {
        Tenant = await repo.TenantBySlugAsync(slug.ToLowerInvariant());
        if (Tenant is null) return NotFound();
        if (!Tenant.IsActive) { Error = "This PayLink account is suspended. Contact your platform administrator."; return Page(); }

        var current = await HttpContext.AuthenticateAsync(AuthSchemes.Console);
        if (current.Succeeded && current.Principal?.Get(PayLinkClaims.Tenant) == Tenant.Slug) return Redirect($"/{Tenant.Slug}/console");
        if (!go) return Page();

        var ll = await settings.GetLoginLinkAsync();
        if (ll is null) { Error = "LoginLink isn't connected on this platform."; return Page(); }
        try
        {
            var url = await oidc.BeginAsync(HttpContext, ll.ServerUrl, LoginLinkClient.DiscoveryUrl(ll.ServerUrl, Tenant.LoginlinkOrg),
                Tenant.ConsoleClientId, await settings.GetPublicBaseUrlAsync() + "/auth/callback", "tenant:" + Tenant.Slug, $"/{Tenant.Slug}/login");
            return Redirect(url);
        }
        catch (LoginLinkException ex)
        {
            Error = ex.Message;
            return Page();
        }
    }
}
