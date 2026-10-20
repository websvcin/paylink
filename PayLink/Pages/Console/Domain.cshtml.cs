using Microsoft.AspNetCore.Mvc;
using PayLink.Platform;
using PayLink.Tenancy;

namespace PayLink.Pages.Console;

public sealed class DomainModel(CustomDomains domains, PlatformSettings settings) : ConsolePage
{
    public TenantDomain? Domain { get; private set; }
    /// <summary>Where the domain should point: the host in the platform's public address.</summary>
    public string Target { get; private set; } = "";

    public async Task OnGetAsync()
    {
        Domain = await domains.ForTenantAsync(Tenant.Id);
        Target = (await settings.GetPublicBaseUrlAsync()) is { Length: > 0 } b ? new Uri(b).Host : "";
    }

    public async Task<IActionResult> OnPostAddAsync(string? domain)
    {
        if (!CanConfigure) return NotAllowed("change the domain");
        var (added, problem) = await domains.AddAsync(Tenant, domain, Me);
        return problem is not null ? Failed(problem) : Done($"Now add the DNS records below for {added!.Domain}, then press Verify.");
    }

    public async Task<IActionResult> OnPostVerifyAsync()
    {
        if (!CanConfigure) return NotAllowed("change the domain");
        var (ok, message) = await domains.VerifyAsync(Tenant, Me);
        return ok ? Done(message) : Failed(message);
    }

    public async Task<IActionResult> OnPostRemoveAsync()
    {
        if (!CanConfigure) return NotAllowed("change the domain");
        await domains.RemoveAsync(Tenant, Me);
        return Done("Domain removed. Your console, links and checkout use the platform's address again.");
    }
}
