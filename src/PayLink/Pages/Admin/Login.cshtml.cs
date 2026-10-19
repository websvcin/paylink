using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PayLink.Identity;
using PayLink.Platform;

namespace PayLink.Pages.Admin;

[AllowAnonymous]
public sealed class LoginModel(PlatformSettings settings, OidcSignIn oidc) : PageModel
{
    public string? Error { get; private set; }

    public async Task<IActionResult> OnGetAsync(string? returnUrl)
    {
        var current = await HttpContext.AuthenticateAsync(AuthSchemes.Admin);
        if (current.Succeeded) return LocalRedirect(Url.IsLocalUrl(returnUrl) ? returnUrl! : "/admin");

        var ll = await settings.GetLoginLinkAsync();
        if (ll is null) { Error = "LoginLink isn't connected yet."; return Page(); }
        try
        {
            var url = await oidc.BeginAsync(HttpContext, ll.ServerUrl, LoginLinkClient.DiscoveryUrl(ll.ServerUrl, ll.PlatformOrg),
                ll.AdminClientId, await settings.GetPublicBaseUrlAsync() + "/auth/callback", "admin",
                Url.IsLocalUrl(returnUrl) ? returnUrl : "/admin");
            return Redirect(url);
        }
        catch (LoginLinkException ex)
        {
            Error = ex.Message;
            return Page();
        }
    }
}
