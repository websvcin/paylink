using Microsoft.AspNetCore.Mvc;
using PayLink.Platform;
using PayLink.Tenancy;

namespace PayLink.Pages.Admin;

public sealed class TenantsModel(PlatformRepository repo, PlatformDb platformDb, TenantDb tenantDb) : AdminPage
{
    [BindProperty(SupportsGet = true)] public string? Q { get; set; }
    public IReadOnlyList<TenantRecord> Tenants { get; private set; } = [];

    public async Task OnGetAsync() => Tenants = await repo.TenantsAsync(Q);

    public async Task<IActionResult> OnPostStatusAsync(long id, string status)
    {
        if (status is not ("active" or "suspended")) return Failed("Unknown status.");
        var tenant = await repo.TenantByIdAsync(id);
        if (tenant is null) return Failed("That tenant doesn't exist.");
        await repo.SetTenantStatusAsync(id, status);
        var action = status == "active" ? "tenant.reactivated" : "tenant.suspended";
        await platformDb.AuditAsync(Me, action, tenant.Slug);
        await tenantDb.AuditAsync(tenant, Me, action, tenant.Slug, "by platform admin");
        return Done(status == "active" ? $"{tenant.Name} is active again." : $"{tenant.Name} is suspended. Its team can't sign in until you reactivate it.");
    }
}
