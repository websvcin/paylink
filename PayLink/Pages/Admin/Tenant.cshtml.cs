using Microsoft.AspNetCore.Mvc;
using PayLink.Identity;
using PayLink.Infrastructure;
using PayLink.Platform;
using PayLink.Tenancy;

namespace PayLink.Pages.Admin;

/// <summary>One tenant: where its console lives (path or subdomain) and where its data lives (move to another engine).</summary>
public sealed class TenantModel(PlatformRepository repo, PlatformSettings settings, PlatformDb platformDb, TenantDb tenantDb, DatabaseMoves moves,
    LoginLinkClient loginLink, SecretProtector secrets, BootstrapStore bootstrap) : AdminPage
{
    [FromRoute] public string Slug { get; set; } = "";
    [BindProperty] public ServerDbInput Byo { get; set; } = new();

    public TenantRecord Tenant { get; private set; } = null!;
    public string TenantDomain { get; private set; } = "";
    public string PublicBaseUrl { get; private set; } = "";
    public bool HostingReady { get; private set; }
    public string SubdomainUrl => $"{(PublicBaseUrl.StartsWith("http://") ? "http" : "https")}://{Tenant.Slug}.{TenantDomain}";

    private async Task<bool> LoadAsync()
    {
        if (await repo.TenantBySlugAsync(Slug.ToLowerInvariant()) is not { } t) return false;
        Tenant = t;
        TenantDomain = await settings.GetTenantDomainAsync();
        PublicBaseUrl = await settings.GetPublicBaseUrlAsync();
        HostingReady = (await bootstrap.GetHostingAsync()).Kind != WebSvc.PayLink.Storage.Provisioning.HostingKind.None;
        return true;
    }

    public async Task<IActionResult> OnGetAsync() => await LoadAsync() ? Page() : NotFound();

    public async Task<IActionResult> OnPostConsoleAsync(string mode)
    {
        if (!await LoadAsync()) return NotFound();
        if (mode is not ("path" or "subdomain")) return Failed("Unknown console address.");
        string note = "";
        if (mode == "subdomain")
        {
            if (TenantDomain.Length == 0) return Failed("Set the tenant domain first (Admin → Platform settings), e.g. paylink.websvc.in, with wildcard DNS and a wildcard certificate.");
            // The console app in the tenant's LoginLink organization must accept the subdomain's callback.
            var callbacks = new[] { $"{PublicBaseUrl}/auth/callback", $"{SubdomainUrl}/auth/callback" };
            if (Tenant.MgmtClientId is { } id && Tenant.MgmtClientSecretEnc is { } enc && Tenant.ConsoleAppId is { Length: > 0 } appId && await settings.GetLoginLinkAsync() is { } ll)
            {
                try
                {
                    var config = await loginLink.GetDiscoveryAsync(LoginLinkClient.DiscoveryUrl(ll.ServerUrl, Tenant.LoginlinkOrg));
                    var token = await loginLink.ClientCredentialsTokenAsync(config.TokenEndpoint, id, secrets.Unprotect(enc));
                    await loginLink.SetAppRedirectUrisAsync(ll.ServerUrl, token, appId, callbacks);
                    note = " LoginLink now also accepts its sign-in callback.";
                }
                catch (LoginLinkException ex) { return Failed("LoginLink didn't accept the subdomain's callback: " + ex.Message); }
            }
            else note = $" PayLink has no management credential for {Tenant.LoginlinkOrg}: add {callbacks[1]} to the PayLink console app's redirect URIs in LoginLink.";
        }
        await repo.SetTenantConsoleModeAsync(Tenant.Id, mode);
        TenantHosts.Forget();
        await platformDb.AuditAsync(Me, "tenant.console_address_changed", Tenant.Slug, mode);
        return Done(mode == "subdomain" ? $"{Tenant.Name}'s console is at {SubdomainUrl}.{note}" : $"{Tenant.Name}'s console is at /{Tenant.Slug}/console.");
    }

    public async Task<IActionResult> OnPostMoveAsync(string to)
    {
        if (!await LoadAsync()) return NotFound();
        WebSvc.PayLink.Storage.DatabaseTarget? own = null;
        if (to == "byo" && !Byo.TryBuild(out own, out var error)) return Failed(error);
        var result = await moves.MoveTenantAsync(Tenant, to, own, Me, HttpContext.RequestAborted);
        return result.Ok ? Done(result.Message) : Failed(result.Message);
    }
}
