using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PayLink.Identity;
using PayLink.Infrastructure;
using PayLink.Platform;
using WebSvc.PayLink.Storage;
using WebSvc.PayLink.Storage.Provisioning;

namespace PayLink.Pages.Setup;

public sealed class LoginLinkInput
{
    public string? ServerUrl { get; set; }
    public string? PlatformOrg { get; set; }
    public string? PublicBaseUrl { get; set; }
    public string AppMode { get; set; } = "register";      // register | manual
    public string? MgmtClientId { get; set; }
    public string? MgmtClientSecret { get; set; }
    public string? ClientId { get; set; }
    public string? ClientSecret { get; set; }
    public string? PlatformApiClientId { get; set; }
    public string? PlatformApiClientSecret { get; set; }
}

/// <summary>
/// First-run wizard: unlock with the setup code, database hosting (optional), platform database,
/// LoginLink (required), then the first platform admin signs in through LoginLink.
/// </summary>
public sealed class IndexModel(
    BootstrapStore bootstrap,
    PlatformDb platformDb,
    PlatformSettings settings,
    LoginLinkClient loginLink,
    OidcSignIn oidc,
    AppPaths paths,
    IHttpClientFactory httpFactory,
    IDataProtectionProvider dataProtection) : PageModel
{
    public const string UnlockCookie = "paylink.setup";
    private const string HostingDone = "setup_hosting_done";

    public static readonly (string Key, string Title)[] Steps =
    [
        ("unlock", "Unlock"), ("hosting", "Database hosting"), ("database", "Platform database"), ("loginlink", "LoginLink"), ("admin", "First admin"),
    ];

    [BindProperty(SupportsGet = true)] public string? Step { get; set; }
    [BindProperty] public string? Code { get; set; }
    [BindProperty] public HostingInput Hosting { get; set; } = new();
    [BindProperty] public string DbChoice { get; set; } = "sqlite";
    [BindProperty] public MySqlInput Mysql { get; set; } = new();
    [BindProperty] public LoginLinkInput LL { get; set; } = new();

    public bool Unlocked { get; private set; }
    public string DataRoot => paths.DataRoot;
    public HostingSettings SavedHosting { get; private set; } = new();
    public bool HostingCompleted { get; private set; }
    public DatabaseTarget? PlatformTarget { get; private set; }
    public LoginLinkSettings? SavedLoginLink { get; private set; }
    public string? Message { get; private set; }
    public string? Error { get; private set; }
    public string DetectedBaseUrl => $"{Request.Scheme}://{Request.Host}";

    public bool IsDone(string step) => step switch
    {
        "unlock" => Unlocked,
        "hosting" => HostingCompleted,
        "database" => PlatformTarget is not null,
        "loginlink" => SavedLoginLink is not null,
        _ => false,
    };

    private IDataProtector Protector => dataProtection.CreateProtector("PayLink.Setup.v1");

    private async Task LoadAsync()
    {
        var code = await bootstrap.GetOrCreateSetupCodeAsync();
        Unlocked = Request.Cookies.TryGetValue(UnlockCookie, out var raw) && TryUnprotect(raw) == code;
        SavedHosting = await bootstrap.GetHostingAsync();
        HostingCompleted = await bootstrap.GetValueAsync(HostingDone) == "true";
        PlatformTarget = await bootstrap.GetPlatformDatabaseAsync();
        if (PlatformTarget is not null)
        {
            try { SavedLoginLink = await settings.GetLoginLinkAsync(); }
            catch (Exception ex) { Error = "The platform database can't be read: " + ex.Message; }
        }

        var first = Steps.Select(s => s.Key).FirstOrDefault(k => !IsDone(k)) ?? "admin";
        var index = Array.FindIndex(Steps, s => s.Key == Step);
        var firstIndex = Array.FindIndex(Steps, s => s.Key == first);
        // Steps can be revisited, but not skipped ahead of an unfinished one.
        if (index < 0 || index > firstIndex || (!Unlocked && Step != "unlock")) Step = Unlocked ? first : "unlock";

        if (Step == "hosting" && Hosting.Kind == HostingKind.None && Request.Method == "GET") Hosting = HostingInput.From(SavedHosting);
        if (Step == "loginlink" && Request.Method == "GET")
        {
            LL.ServerUrl ??= SavedLoginLink?.ServerUrl ?? "https://loginlink.websvc.in";
            LL.PlatformOrg ??= SavedLoginLink?.PlatformOrg;
            LL.PublicBaseUrl ??= await SafeBaseUrl() ?? DetectedBaseUrl;
        }
    }

    private async Task<string?> SafeBaseUrl()
    {
        try { return PlatformTarget is null ? null : (await settings.GetPublicBaseUrlAsync()).NullIfEmpty(); }
        catch { return null; }
    }

    private string? TryUnprotect(string raw)
    {
        try { return Protector.Unprotect(raw); }
        catch (CryptographicException) { return null; }
    }

    public async Task OnGetAsync() => await LoadAsync();

    public async Task<IActionResult> OnPostUnlockAsync()
    {
        var code = await bootstrap.GetOrCreateSetupCodeAsync();
        if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes((Code ?? "").Trim().ToUpperInvariant()), Encoding.UTF8.GetBytes(code)))
        {
            await LoadAsync();
            Error = "That setup code doesn't match. It's printed in PayLink's startup log.";
            return Page();
        }
        Response.Cookies.Append(UnlockCookie, Protector.Protect(code), new CookieOptions
        {
            HttpOnly = true, Secure = Request.IsHttps, SameSite = SameSiteMode.Lax, MaxAge = TimeSpan.FromHours(12),
        });
        return Redirect("/setup?step=hosting");
    }

    public async Task<IActionResult> OnPostHostingAsync(string action)
    {
        await LoadAsync();
        if (!Unlocked) return Redirect("/setup");
        Step = "hosting";

        if (action == "skip")
        {
            await bootstrap.SetHostingAsync(new HostingSettings());
            await bootstrap.SetValueAsync(HostingDone, "true");
            return Redirect("/setup?step=database");
        }
        if (Hosting.Validate() is { } problem) { Error = problem; return Page(); }

        var next = Hosting.ToSettings(SavedHosting);
        if (action == "test")
        {
            var provisioner = DatabaseProvisioners.Create(next, httpFactory.CreateClient("hosting"));
            var check = provisioner is null ? new ProvisioningCheck(false, "Choose a hosting environment.") : await provisioner.TestAsync();
            if (check.Ok) Message = check.Message; else Error = check.Message;
            return Page();
        }

        await bootstrap.SetHostingAsync(next);
        await bootstrap.SetValueAsync(HostingDone, "true");
        return Redirect("/setup?step=database");
    }

    public async Task<IActionResult> OnPostDatabaseAsync(string action)
    {
        await LoadAsync();
        if (!Unlocked) return Redirect("/setup");
        Step = "database";

        DatabaseTarget? target;
        ProvisionedDatabase? created = null;
        switch (DbChoice)
        {
            case "sqlite":
                target = DatabaseTarget.ForSqliteFile(paths.PlatformSqliteFile);
                break;
            case "mysql":
                if (!Mysql.TryBuild(out target, out var parseError)) { Error = parseError; return Page(); }
                break;
            case "mysql-create":
                var provisioner = DatabaseProvisioners.Create(SavedHosting, httpFactory.CreateClient("hosting"));
                if (provisioner is null) { Error = "Set up database hosting first, or choose another option."; return Page(); }
                if (action == "test") { var c = await provisioner.TestAsync(); if (c.Ok) Message = c.Message; else Error = c.Message; return Page(); }
                try { created = await provisioner.CreateAsync("paylink_", "platform"); }
                catch (ProvisioningException ex) { Error = ex.Message; return Page(); }
                target = created.Target;
                break;
            default:
                Error = "Choose where the platform database lives.";
                return Page();
        }

        var probe = await DatabaseProbe.TestAsync(target!);
        if (!probe.Ok) { Error = probe.Message; return Page(); }
        var existingVersion = await SqlMigrator.CurrentVersionAsync(target!);
        if (existingVersion is null && !probe.IsEmpty)
        {
            Error = $"That database already has {probe.TableCount} table(s) that aren't PayLink's. Use an empty database, or an existing PayLink platform database.";
            return Page();
        }
        if (action == "test")
        {
            Message = existingVersion is null ? probe.Message : $"Connected. This is an existing PayLink platform database (schema {existingVersion}); setup will reattach to it.";
            return Page();
        }

        try
        {
            await platformDb.MigrateAsync(target);
        }
        catch (Exception ex)
        {
            if (created is not null) await DatabaseProvisioners.Create(SavedHosting, httpFactory.CreateClient("hosting"))!.DropAsync(created);
            Error = "Creating the platform tables failed: " + ex.Message;
            return Page();
        }
        await bootstrap.SetPlatformDatabaseAsync(target!);
        platformDb.Reset();
        await platformDb.AuditAsync(Actor.System, existingVersion is null ? "setup.platform_database_created" : "setup.platform_database_attached", target!.Describe());
        return Redirect("/setup?step=loginlink");
    }

    public async Task<IActionResult> OnPostLoginLinkAsync()
    {
        await LoadAsync();
        if (!Unlocked) return Redirect("/setup");
        if (PlatformTarget is null) return Redirect("/setup?step=database");
        Step = "loginlink";

        var server = LoginLinkClient.NormalizeServer(LL.ServerUrl ?? "");
        var baseUrl = (LL.PublicBaseUrl ?? DetectedBaseUrl).Trim().TrimEnd('/');
        var org = (LL.PlatformOrg ?? "").Trim().ToLowerInvariant();
        if (!Uri.TryCreate(server, UriKind.Absolute, out _)) { Error = "Enter the LoginLink server address, e.g. https://loginlink.websvc.in"; return Page(); }
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out _)) { Error = "Enter PayLink's public address, e.g. https://paylink.websvc.in"; return Page(); }

        try
        {
            var discoveryUrl = LoginLinkClient.DiscoveryUrl(server, org);
            var config = await loginLink.GetDiscoveryAsync(discoveryUrl);
            var redirectUri = baseUrl + "/auth/callback";

            RegisteredApp app;
            if (LL.AppMode == "manual")
            {
                if (string.IsNullOrWhiteSpace(LL.ClientId) || string.IsNullOrWhiteSpace(LL.ClientSecret))
                { Error = "Enter the client ID and secret of the app you registered in LoginLink."; return Page(); }
                app = new RegisteredApp("", LL.ClientId.Trim(), LL.ClientSecret.Trim());
            }
            else
            {
                if (string.IsNullOrWhiteSpace(LL.MgmtClientId) || string.IsNullOrWhiteSpace(LL.MgmtClientSecret))
                { Error = "Enter a LoginLink management credential (client ID and secret) with Register apps."; return Page(); }
                var token = await loginLink.ClientCredentialsTokenAsync(config.TokenEndpoint, LL.MgmtClientId.Trim(), LL.MgmtClientSecret.Trim());
                app = await loginLink.RegisterAppAsync(server, token, "PayLink admin", redirectUri);
            }

            var saved = new LoginLinkSettings
            {
                ServerUrl = server, PlatformOrg = org, AdminAppId = app.AppId, AdminClientId = app.ClientId, AdminClientSecret = app.ClientSecret,
            };
            if (!string.IsNullOrWhiteSpace(LL.PlatformApiClientId) && !string.IsNullOrWhiteSpace(LL.PlatformApiClientSecret))
            {
                var root = await loginLink.GetDiscoveryAsync(LoginLinkClient.DiscoveryUrl(server, null));
                await loginLink.ClientCredentialsTokenAsync(root.TokenEndpoint, LL.PlatformApiClientId.Trim(), LL.PlatformApiClientSecret.Trim());
                saved.PlatformApiClientId = LL.PlatformApiClientId.Trim();
                saved.PlatformApiClientSecret = LL.PlatformApiClientSecret.Trim();
            }

            await settings.SetAsync(PlatformSettings.KeyPublicBaseUrl, baseUrl);
            await settings.SetLoginLinkAsync(saved);
            await platformDb.AuditAsync(Actor.System, "setup.loginlink_connected", string.IsNullOrEmpty(org) ? server : $"{server}/{org}",
                LL.AppMode == "manual" ? "app entered by hand" : $"app {app.AppId} registered");
        }
        catch (LoginLinkException ex)
        {
            Error = ex.Message;
            return Page();
        }
        return Redirect("/setup?step=admin");
    }

    public async Task<IActionResult> OnPostSignInAsync()
    {
        await LoadAsync();
        if (!Unlocked || SavedLoginLink is null) return Redirect("/setup");
        var baseUrl = await settings.GetPublicBaseUrlAsync();
        try
        {
            var url = await oidc.BeginAsync(HttpContext, SavedLoginLink.ServerUrl,
                LoginLinkClient.DiscoveryUrl(SavedLoginLink.ServerUrl, SavedLoginLink.PlatformOrg),
                SavedLoginLink.AdminClientId, baseUrl + "/auth/callback", "setup-admin");
            return Redirect(url);
        }
        catch (LoginLinkException ex)
        {
            Step = "admin";
            Error = ex.Message;
            return Page();
        }
    }
}

internal static class StringExtensions
{
    public static string? NullIfEmpty(this string? value) => string.IsNullOrEmpty(value) ? null : value;
}
