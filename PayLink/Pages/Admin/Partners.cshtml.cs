using Microsoft.AspNetCore.Mvc;
using PayLink.Payments;
using PayLink.Platform;
using WebSvc.PayLink.Abstractions;

namespace PayLink.Pages.Admin;

/// <summary>
/// Partner onboarding: for each payment method, whether tenants are offered help getting an account (a partner referral link or a
/// form the platform team follows up), and every tenant's request with its status.
/// </summary>
public sealed class PartnersModel(ConnectorRegistry registry, PartnerOnboarding partners, PlatformRepository repo) : AdminPage
{
    [BindProperty(SupportsGet = true)] public string? Status { get; set; }
    [BindProperty(SupportsGet = true)] public string? Method { get; set; }

    /// <summary>Methods a tenant needs an outside account for (not the Simulator, not free PayLink UPI).</summary>
    public IReadOnlyList<IPaymentConnector> Methods => registry.All.Where(c => !c.Manifest.TestOnly && c.Manifest.Id != "upi").ToList();
    public IReadOnlyDictionary<string, PartnerProgram> Programs { get; private set; } = new Dictionary<string, PartnerProgram>();
    public IReadOnlyList<PartnerRequest> Requests { get; private set; } = [];
    public IReadOnlyDictionary<long, TenantRecord> Tenants { get; private set; } = new Dictionary<long, TenantRecord>();

    public string MethodName(string id) => registry.Get(id)?.Manifest.Name ?? id;

    public async Task OnGetAsync()
    {
        Programs = await partners.ProgramsAsync();
        Requests = await partners.ListAsync(Status, Method);
        Tenants = (await repo.TenantsAsync(take: 10000)).ToDictionary(t => t.Id);
    }

    public async Task<IActionResult> OnPostProgramAsync(string connector, string mode, string? headline, string? pitch, string? link)
    {
        if (registry.Get(connector) is not { } c) return Failed("Unknown payment method.");
        if (await partners.SaveProgramAsync(connector, mode, headline, pitch, link, Me) is { } problem) return Failed($"{c.Manifest.Name}: {problem}");
        return Done(mode switch
        {
            PartnerModes.Link => $"{c.Manifest.Name}: tenants see \"Get an account\", which records their interest and opens the partner link.",
            PartnerModes.Form => $"{c.Manifest.Name}: tenants see \"Get an account\" with a short form; requests appear below.",
            _ => $"{c.Manifest.Name}: no partner programme; tenants bring their own account.",
        });
    }

    public async Task<IActionResult> OnPostRequestAsync(long id, string status, string? note) =>
        await partners.UpdateAsync(id, status, note, Me) ? Done("Request updated. The tenant sees the new status on Payment methods.") : Failed("Unknown request or status.");
}
