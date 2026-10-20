using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PayLink.Identity;
using PayLink.Infrastructure;
using PayLink.Platform;
using PayLink.Tenancy;
using WebSvc.PayLink.Storage;

namespace PayLink.Pages.Signup;

/// <summary>
/// Creating a PayLink tenant: connect an existing LoginLink organization (or create one), prove you're its admin by
/// signing in, then choose the name, address and where the data lives.
/// </summary>
public sealed class IndexModel(
    PlatformSettings settings,
    PlatformRepository platform,
    SignupSessions signups,
    OrgConnector orgs,
    TenantProvisioning provisioning,
    OidcSignIn oidc) : PageModel
{
    [BindProperty] public string? OrgSlug { get; set; }
    [BindProperty] public string? MgmtClientId { get; set; }
    [BindProperty] public string? MgmtClientSecret { get; set; }

    [BindProperty] public string? NewOrgName { get; set; }
    [BindProperty] public string? AdminEmail { get; set; }
    [BindProperty] public string? AdminName { get; set; }
    [BindProperty] public string? AdminPassword { get; set; }

    [BindProperty] public string? TenantName { get; set; }
    [BindProperty] public string? TenantSlug { get; set; }
    [BindProperty] public string DbChoice { get; set; } = "sqlite";
    [BindProperty] public ServerDbInput Byo { get; set; } = new();

    public bool SignupOpen { get; private set; }
    public bool CanCreateOrganizations { get; private set; }
    public string LoginLinkServer { get; private set; } = "";
    public SignupSession? Session { get; private set; }
    public (bool Allowed, string Reason) AutoProvision { get; private set; }
    public IReadOnlySet<string> DbOptions { get; private set; } = new HashSet<string>();
    public string? Error { get; private set; }
    public string? Message { get; private set; }
    public string Step => Session?.Org is null ? "org" : !Session.Verified ? "verify" : "details";

    private async Task LoadAsync()
    {
        SignupOpen = await settings.IsSignupOpenAsync();
        var ll = await settings.GetLoginLinkAsync();
        CanCreateOrganizations = ll?.CanCreateOrganizations == true;
        LoginLinkServer = ll?.ServerUrl ?? "";
        Session = (await signups.CurrentAsync())?.Session;
        DbOptions = await settings.GetTenantDbOptionsAsync();
        if (Request.Method == "GET" && !DbOptions.Contains(DbChoice)) DbChoice = DbOptions.FirstOrDefault() ?? TenantDbOptions.Sqlite;
        if (Session?.Org is { } org)
        {
            AutoProvision = await provisioning.CanAutoProvisionAsync(org.OrgSlug);
            if (Request.Method == "GET")
            {
                TenantName ??= Session.SuggestedName ?? org.OrgSlug;
                TenantSlug ??= Slugs.Suggest(TenantName);
            }
        }
    }

    public async Task OnGetAsync() => await LoadAsync();

    public async Task<IActionResult> OnPostConnectExistingAsync()
    {
        await LoadAsync();
        if (!SignupOpen) return Page();
        if (string.IsNullOrWhiteSpace(OrgSlug) || string.IsNullOrWhiteSpace(MgmtClientId) || string.IsNullOrWhiteSpace(MgmtClientSecret))
        {
            Error = "Enter the organization slug and a management credential (client ID and secret).";
            return Page();
        }
        try
        {
            var org = await orgs.ConnectExistingAsync(OrgSlug, MgmtClientId.Trim(), MgmtClientSecret.Trim());
            var id = await signups.StartAsync(new SignupSession { Org = org });
            return Redirect(await BeginSignInAsync(id, org));
        }
        catch (Exception ex) when (ex is LoginLinkException or TenantCreateException)
        {
            Error = ex.Message;
            return Page();
        }
    }

    public async Task<IActionResult> OnPostCreateNewAsync()
    {
        await LoadAsync();
        if (!SignupOpen || !CanCreateOrganizations) return Page();
        if (string.IsNullOrWhiteSpace(NewOrgName) || string.IsNullOrWhiteSpace(AdminEmail) || string.IsNullOrWhiteSpace(AdminPassword))
        {
            Error = "Enter the business name, your email and a password for your new LoginLink account.";
            return Page();
        }
        try
        {
            // The password goes to LoginLink only; PayLink never stores it.
            var org = await orgs.CreateNewAsync(NewOrgName.Trim(), AdminEmail.Trim(), AdminName?.Trim(), AdminPassword);
            var id = await signups.StartAsync(new SignupSession { Org = org, SuggestedName = NewOrgName.Trim() });
            return Redirect(await BeginSignInAsync(id, org));
        }
        catch (Exception ex) when (ex is LoginLinkException or TenantCreateException)
        {
            Error = ex.Message;
            return Page();
        }
    }

    public async Task<IActionResult> OnPostSignInAgainAsync()
    {
        var current = await signups.CurrentAsync();
        if (current?.Session.Org is not { } org) return Redirect("/signup");
        try { return Redirect(await BeginSignInAsync(current.Value.Id, org)); }
        catch (LoginLinkException ex) { await LoadAsync(); Error = ex.Message; return Page(); }
    }

    public async Task<IActionResult> OnPostRestartAsync()
    {
        await signups.EndAsync();
        return Redirect("/signup");
    }

    public async Task<IActionResult> OnPostCreateAsync(string action)
    {
        await LoadAsync();
        var current = await signups.CurrentAsync();
        if (current is not { } c || c.Session is not { Verified: true, Org: { } org } session) return Redirect("/signup");

        if (!DbOptions.Contains(DbChoice)) { Error = "That database option isn't offered on this platform. Choose another."; return Page(); }
        var mode = DbChoice switch { "auto" => DatabaseMode.Auto, "byo" => DatabaseMode.Byo, _ => DatabaseMode.Sqlite };
        DatabaseTarget? byo = null;
        if (mode == DatabaseMode.Byo && !Byo.TryBuild(out byo, out var parseError)) { Error = parseError; return Page(); }

        if (action == "test")
        {
            if (mode != DatabaseMode.Byo) { Message = "Nothing to test for this option."; return Page(); }
            var probe = await DatabaseProbe.TestAsync(byo!);
            if (!probe.Ok) Error = probe.Message;
            else if (!probe.IsEmpty) Error = $"Connected, but the database already has {probe.TableCount} table(s). PayLink needs an empty database.";
            else if (!probe.CanCreateTables) Error = probe.Message;
            else Message = "Connected. The database is empty and PayLink can create its tables.";
            return Page();
        }

        try
        {
            var tenant = await provisioning.CreateAsync(new TenantCreateRequest(
                TenantName ?? "", TenantSlug ?? "", org, new DatabaseChoice(mode, byo),
                new NewOwner(session.Subject!, session.Email, session.PersonName), "signup",
                Actor.Person(session.Subject!, session.Email ?? session.PersonName, HttpContext.Connection.RemoteIpAddress?.ToString())));

            await signups.EndAsync();
            await SignInHelper.SignInConsoleAsync(HttpContext,
                new SignedInPerson(session.Subject!, session.Email, session.PersonName, "tenant_admin", [], org.OrgSlug, org.Issuer),
                tenant.Slug, TenantRoles.Owner);
            TempData["Ok"] = $"{tenant.Name} is ready. Its data lives in {tenant.DbDisplay}.";
            return Redirect($"/{tenant.Slug}/console");
        }
        catch (TenantCreateException ex)
        {
            Error = ex.Message;
            return Page();
        }
    }

    public async Task<JsonResult> OnGetSlugAsync(string slug)
    {
        slug = (slug ?? "").Trim().ToLowerInvariant();
        if (Slugs.Problem(slug) is { } problem) return new JsonResult(new { ok = false, message = problem });
        var taken = await platform.TenantBySlugAsync(slug) is not null;
        return new JsonResult(new { ok = !taken, message = taken ? $"{slug} is taken." : $"{slug} is available." });
    }

    private async Task<string> BeginSignInAsync(string sessionId, OrgConnection org)
    {
        var ll = await settings.GetLoginLinkAsync() ?? throw new LoginLinkException("LoginLink isn't connected.");
        return await oidc.BeginAsync(HttpContext, ll.ServerUrl, org.DiscoveryUrl, org.ClientId,
            await orgs.CallbackUrlAsync(), "signup:" + sessionId, "/signup");
    }
}
