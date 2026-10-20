using Microsoft.AspNetCore.Mvc;
using PayLink.Tenancy;

namespace PayLink.Pages.Console;

/// <summary>Data access: the built-in reporting API, and connecting CrudApi for full read-only table access.</summary>
public sealed class DataModel(CrudApiConnection crudApi, CustomDomains domains) : ConsolePage
{
    public CrudApiLink? Link { get; private set; }
    public string? Unavailable { get; private set; }
    public bool CanCreateUser { get; private set; }
    public string DefaultHost { get; private set; } = "";
    public string BaseUrl { get; private set; } = "";

    public async Task OnGetAsync()
    {
        Unavailable = crudApi.Unavailable(Tenant);
        Link = Unavailable is null ? await crudApi.GetAsync(Tenant) : null;
        CanCreateUser = Unavailable is null && await crudApi.CanCreateUserAsync(Tenant);
        DefaultHost = Unavailable is null ? new MySqlConnector.MySqlConnectionStringBuilder(TenantDb.Target(Tenant).ConnectionString).Server : "";
        BaseUrl = await domains.BaseUrlAsync(Tenant);
    }

    public async Task<IActionResult> OnPostReaderAsync(string? user, string? password, string? host)
    {
        if (!CanConfigure) return NotAllowed("connect CrudApi");
        return await crudApi.SetReaderAsync(Tenant, user, password, host, Me) is { } problem ? Failed(problem) : Done("Read-only user checked: it can read and can't change anything.");
    }

    public async Task<IActionResult> OnPostRegisterAsync(string? server, string? workspace, string? key)
    {
        if (!CanConfigure) return NotAllowed("connect CrudApi");
        var (ok, message, secret) = await crudApi.RegisterAsync(Tenant, server, workspace, key, Me);
        if (secret is not null) TempData["Secret"] = secret;
        return ok ? Done(message) : Failed(message);
    }

    public async Task<IActionResult> OnPostDisconnectAsync()
    {
        if (!CanConfigure) return NotAllowed("connect CrudApi");
        return Done(await crudApi.DisconnectAsync(Tenant, Me));
    }
}
