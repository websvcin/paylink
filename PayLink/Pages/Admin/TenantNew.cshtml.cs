using Microsoft.AspNetCore.Mvc;
using PayLink.Identity;
using PayLink.Infrastructure;
using PayLink.Tenancy;
using WebSvc.PayLink.Storage;

namespace PayLink.Pages.Admin;

/// <summary>A platform admin creates a tenant on someone's behalf, connecting their existing LoginLink organization.</summary>
public sealed class TenantNewModel(OrgConnector orgs, TenantProvisioning provisioning, BootstrapStore bootstrap) : AdminPage
{
    [BindProperty] public string? Name { get; set; }
    [BindProperty] public string? Slug { get; set; }
    [BindProperty] public string? OrgSlug { get; set; }
    [BindProperty] public string? MgmtClientId { get; set; }
    [BindProperty] public string? MgmtClientSecret { get; set; }
    [BindProperty] public string DbChoice { get; set; } = "sqlite";
    [BindProperty] public MySqlInput Byo { get; set; } = new();

    public bool HostingConfigured { get; private set; }
    public string? Error { get; private set; }

    public async Task OnGetAsync() => HostingConfigured = (await bootstrap.GetHostingAsync()).Kind != WebSvc.PayLink.Storage.Provisioning.HostingKind.None;

    public async Task<IActionResult> OnPostAsync()
    {
        await OnGetAsync();
        if (string.IsNullOrWhiteSpace(OrgSlug) || string.IsNullOrWhiteSpace(MgmtClientId) || string.IsNullOrWhiteSpace(MgmtClientSecret))
        {
            Error = "Enter the tenant's LoginLink organization slug and a management credential from it.";
            return Page();
        }
        var mode = DbChoice switch { "auto" => DatabaseMode.Auto, "byo" => DatabaseMode.Byo, _ => DatabaseMode.Sqlite };
        DatabaseTarget? byo = null;
        if (mode == DatabaseMode.Byo && !Byo.TryBuild(out byo, out var parseError)) { Error = parseError; return Page(); }

        if (await provisioning.CheckNameAndSlugAsync(Name, Slug) is { } problem) { Error = problem; return Page(); }
        try
        {
            var org = await orgs.ConnectExistingAsync(OrgSlug, MgmtClientId.Trim(), MgmtClientSecret.Trim());
            // A platform admin may create a MySQL database for anyone; the approval list only limits self-service signup.
            var tenant = await provisioning.CreateAsync(new TenantCreateRequest(
                Name ?? "", Slug ?? "", org, new DatabaseChoice(mode, byo), null, "admin", Me), bypassApprovalList: true);
            TempData["Ok"] = $"{tenant.Name} is ready at /{tenant.Slug}/console. Admins of the LoginLink organization \"{tenant.LoginlinkOrg}\" are let in when they sign in.";
            return Redirect("/admin/tenants");
        }
        catch (Exception ex) when (ex is LoginLinkException or TenantCreateException)
        {
            Error = ex.Message;
            return Page();
        }
    }
}
