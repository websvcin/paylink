using Microsoft.AspNetCore.Mvc;
using PayLink.Identity;
using PayLink.Infrastructure;
using PayLink.Platform;
using PayLink.Tenancy;

namespace PayLink.Pages.Admin;

/// <summary>One tenant: where its console lives (path or subdomain) and where its data lives (move to another engine).</summary>
public sealed class TenantModel(PlatformRepository repo, PlatformSettings settings, PlatformDb platformDb, TenantDb tenantDb, DatabaseMoves moves,
    ConsoleCallbacks callbacks, CustomDomains domains, BootstrapStore bootstrap, PartnerOnboarding partners, PayLink.Payments.ConnectorRegistry registry) : AdminPage
{
    public IReadOnlyList<PartnerRequest> PartnerRequests { get; private set; } = [];
    public string MethodName(string id) => registry.Get(id)?.Manifest.Name ?? id;
    [FromRoute] public string Slug { get; set; } = "";
    [BindProperty] public ServerDbInput Byo { get; set; } = new();

    public TenantRecord Tenant { get; private set; } = null!;
    public string TenantDomain { get; private set; } = "";
    public string PublicBaseUrl { get; private set; } = "";
    public bool HostingReady { get; private set; }
    public TenantDomain? Domain { get; private set; }
    public string SubdomainUrl => $"{(PublicBaseUrl.StartsWith("http://") ? "http" : "https")}://{Tenant.Slug}.{TenantDomain}";

    private async Task<bool> LoadAsync()
    {
        if (await repo.TenantBySlugAsync(Slug.ToLowerInvariant()) is not { } t) return false;
        Tenant = t;
        TenantDomain = await settings.GetTenantDomainAsync();
        PublicBaseUrl = await settings.GetPublicBaseUrlAsync();
        HostingReady = (await bootstrap.GetHostingAsync()).Kind != WebSvc.PayLink.Storage.Provisioning.HostingKind.None;
        Domain = await domains.ForTenantAsync(t.Id);
        PartnerRequests = await partners.ForTenantAsync(t.Id);
        return true;
    }

    public async Task<IActionResult> OnGetAsync() => await LoadAsync() ? Page() : NotFound();

    public async Task<IActionResult> OnPostConsoleAsync(string mode)
    {
        if (!await LoadAsync()) return NotFound();
        if (mode is not ("path" or "subdomain")) return Failed("Unknown console address.");
        if (mode == "subdomain" && TenantDomain.Length == 0)
            return Failed("Set the tenant domain first (Admin → Platform settings), e.g. paylink.websvc.in, with wildcard DNS and a wildcard certificate.");
        await repo.SetTenantConsoleModeAsync(Tenant.Id, mode);
        TenantHosts.Forget();
        // The console app in the tenant's LoginLink organization must accept the callback of every address the console has.
        Tenant.ConsoleMode = mode;
        var (_, note) = await callbacks.RegisterAsync(Tenant);
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
