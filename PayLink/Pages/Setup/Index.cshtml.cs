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
    /// <summary>"org": people of one organization sign in to /admin; "root": LoginLink's platform root organization.</summary>
    public string AdminScope { get; set; } = "org";
    /// <summary>register (organization management credential) | create-org (new organization via the platform credential) | manual.</summary>
    public string AppMode { get; set; } = "register";
    public string? NewOrgName { get; set; }
    public string? NewOrgAdminEmail { get; set; }
    public string? NewOrgAdminName { get; set; }
    public string? NewOrgAdminPassword { get; set; }
    public string? MgmtClientId { get; set; }
    public string? MgmtClientSecret { get; set; }
    public string? ClientId { get; set; }
    public string? ClientSecret { get; set; }
    public string? PlatformApiClientId { get; set; }
    public string? PlatformApiClientSecret { get; set; }
    // The setup page's other options carry their own copies of these, so each option's fields bind on their own.
    public string? ManualOrg { get; set; }
    public string? RootMgmtClientId { get; set; }
    public string? RootMgmtClientSecret { get; set; }
}

/// <summary>
/// The LoginLink step in progress, one sub-step at a time. Each sub-step is tested before it can be continued, and what passed the
/// test is kept here (encrypted, in bootstrap.db), so secrets don't have to be typed again. Nothing is created in LoginLink until the
/// last sub-step.
/// </summary>
public sealed class LoginLinkDraft
{
    public string? Server { get; set; }
    public string? BaseUrl { get; set; }
    public bool ServerOk { get; set; }
    public string? PlatformId { get; set; }
    public string? PlatformSecret { get; set; }
    public bool PlatformOk { get; set; }
    public bool PlatformSkipped { get; set; }
    /// <summary>register | create-org | manual | root</summary>
    public string OrgMode { get; set; } = "register";
    public string? Org { get; set; }
    public string? MgmtId { get; set; }
    public string? MgmtSecret { get; set; }
    public string? ClientId { get; set; }
    public string? ClientSecret { get; set; }
    public string? NewOrgName { get; set; }
    public string? NewOrgAdminName { get; set; }
    public string? NewOrgAdminEmail { get; set; }
    public string? NewOrgAdminPassword { get; set; }
    public bool OrgOk { get; set; }
}

/// <summary>
/// First-run wizard: unlock with the setup code, new or restore, database hosting (optional), platform database,
/// LoginLink (required, in four sub-steps), then the first platform admin signs in through LoginLink.
/// Every step that takes connection details is tested before Continue unlocks.
/// </summary>
[RequestSizeLimit(BackupService.MaxUploadBytes)]
[RequestFormLimits(MultipartBodyLengthLimit = BackupService.MaxUploadBytes)]
public sealed class IndexModel(
    BootstrapStore bootstrap,
    PlatformDb platformDb,
    PlatformSettings settings,
    LoginLinkClient loginLink,
    OidcSignIn oidc,
    AppPaths paths,
    IHttpClientFactory httpFactory,
    IDataProtectionProvider dataProtection,
    IConfiguration configuration,
    SecretProtector secrets,
    BackupService backups) : PageModel
{
    /// <summary>The LoginLink most installs use; PayLink__LoginLink__DefaultServer changes what setup fills in.</summary>
    public const string PublicLoginLink = "https://loginlink.websvc.in";
    public const string UnlockCookie = "paylink.setup";
    private const string HostingDone = "setup_hosting_done";
    private const string StartChoice = "setup_start";
    private const string DbDraftKey = "setup_db_draft";
    private const string LoginLinkDraftKey = "setup_loginlink_draft";

    public static readonly (string Key, string Title)[] Steps =
    [
        ("unlock", "Data folder & code"), ("start", "New or restore"), ("hosting", "Auto-provisioning"), ("database", "Platform database"), ("loginlink", "LoginLink"), ("admin", "First admin"),
    ];

    public static readonly (string Key, string Title)[] LoginLinkSteps =
    [
        ("server", "5.1 Server"), ("platform", "5.2 Platform credential"), ("org", "5.3 Your organization"), ("admins", "5.4 Who signs in to /admin"),
    ];

    [BindProperty(SupportsGet = true)] public string? Step { get; set; }
    [BindProperty(SupportsGet = true, Name = "ll")] public string? LLStep { get; set; }
    [BindProperty] public string? Code { get; set; }
    [BindProperty] public HostingInput Hosting { get; set; } = new();
    [BindProperty] public string DbChoice { get; set; } = "sqlite";
    [BindProperty] public ServerDbInput Mysql { get; set; } = new();
    [BindProperty] public LoginLinkInput LL { get; set; } = new();
    [BindProperty] public string TenantAutoProvision { get; set; } = AutoProvisionModes.Approved;
    [BindProperty] public string? PlatformDbName { get; set; }
    [BindProperty] public string AdminAdmission { get; set; } = AdmissionModes.ApproveOnly;

    public bool Unlocked { get; private set; }
    public IReadOnlyList<DataFolderItem> DataFolderItems => DataFolder.Describe(paths);
    public string DataRoot => paths.DataRoot;
    public bool DataRootConfigured => paths.IsDataRootConfigured;
    public bool InsideAppFolder => paths.IsInsideAppFolder;
    public bool InContainer => RuntimeEnvironment.InContainer;
    public bool? OnMountedVolume { get; private set; }
    public string? DockerHost { get; private set; }
    public bool IsLocalRequest => RuntimeEnvironment.IsLocalRequest(HttpContext);
    public bool SetupCodeConfigured => bootstrap.SetupCodeIsConfigured;
    public string SetupCodeFile => paths.SetupCodeFile;
    public bool HostingDraftExists { get; private set; }
    public HostingSettings HostingDraftOrSaved { get; private set; } = new();
    public HostingSettings SavedHosting { get; private set; } = new();
    public bool HostingCompleted { get; private set; }
    public bool Started { get; private set; }
    public string KeySource => secrets.Source;
    public string KeyFingerprint => secrets.Fingerprint;
    public DatabaseTarget? PlatformTarget { get; private set; }
    public DatabaseTarget? DbDraft { get; private set; }
    public LoginLinkSettings? SavedLoginLink { get; private set; }
    public LoginLinkDraft Draft { get; private set; } = new();
    public string? Message { get; private set; }
    public string? Warning { get; private set; }
    public string? Error { get; private set; }
    /// <summary>The details on this step passed a test (so Continue is open until something changes).</summary>
    public bool Tested { get; private set; }
    public string DetectedBaseUrl => $"{Request.Scheme}://{Request.Host}";

    public bool IsDone(string step) => step switch
    {
        "unlock" => Unlocked,
        "start" => Started,
        "hosting" => HostingCompleted,
        "database" => PlatformTarget is not null,
        "loginlink" => SavedLoginLink is not null,
        _ => false,
    };

    public bool IsLLDone(string sub) => sub switch
    {
        "server" => Draft.ServerOk || SavedLoginLink is not null,
        "platform" => Draft.PlatformOk || Draft.PlatformSkipped || SavedLoginLink is not null,
        "org" => Draft.OrgOk || SavedLoginLink is not null,
        _ => SavedLoginLink is not null,
    };

    private IDataProtector Protector => dataProtection.CreateProtector("PayLink.Setup.v1");

    private async Task LoadAsync()
    {
        var code = await bootstrap.GetOrCreateSetupCodeAsync();
        Unlocked = Request.Cookies.TryGetValue(UnlockCookie, out var raw) && TryUnprotect(raw) == code;
        // On the server itself (not through a proxy) the code is filled in; from anywhere else it must be typed.
        if (Request.Method == "GET" && IsLocalRequest && !SetupCodeConfigured) Code ??= code;
        OnMountedVolume = RuntimeEnvironment.IsOnMountedVolume(paths.DataRoot);
        DockerHost = RuntimeEnvironment.DockerHostAddress();
        ViewData["DockerHost"] = DockerHost;
        SavedHosting = await bootstrap.GetHostingAsync();
        var draft = await bootstrap.GetHostingDraftAsync();
        HostingDraftExists = draft is not null;
        HostingDraftOrSaved = draft ?? SavedHosting;
        HostingCompleted = await bootstrap.GetValueAsync(HostingDone) == "true";
        PlatformTarget = await bootstrap.GetPlatformDatabaseAsync();
        DbDraft = secrets.UnprotectJson<DatabaseTarget>(await bootstrap.GetValueAsync(DbDraftKey));
        Draft = secrets.UnprotectJson<LoginLinkDraft>(await bootstrap.GetValueAsync(LoginLinkDraftKey)) ?? new LoginLinkDraft();
        Started = await bootstrap.GetValueAsync(StartChoice) is not null || HostingCompleted || PlatformTarget is not null;
        if (PlatformTarget is not null)
        {
            try { SavedLoginLink = await settings.GetLoginLinkAsync(); }
            catch (Exception ex) { Error = "The platform database can't be read: " + ex.Message; }
        }
        if (TempData["SetupWarning"] is string warning) Warning = warning;

        var first = Steps.Select(s => s.Key).FirstOrDefault(k => !IsDone(k)) ?? "admin";
        var index = Array.FindIndex(Steps, s => s.Key == Step);
        var firstIndex = Array.FindIndex(Steps, s => s.Key == first);
        // Steps can be revisited, but not skipped ahead of an unfinished one.
        if (index < 0 || index > firstIndex || (!Unlocked && Step != "unlock")) Step = Unlocked ? first : "unlock";

        if (Step == "hosting" && Request.Method == "GET")
        {
            Hosting = HostingInput.From(draft ?? SavedHosting);
            TenantAutoProvision = await bootstrap.GetInitialTenantAutoProvisionAsync() ?? AutoProvisionModes.Approved;
            // Saved hosting was tested when it was saved; a draft that differs from it hasn't passed yet.
            Tested = HostingCompleted && SavedHosting.Kind != HostingKind.None && draft is null;
        }
        if (Step == "database" && Request.Method == "GET")
        {
            if (DatabaseProvisioners.Create(SavedHosting, httpFactory.CreateClient("hosting")) is { } p) PlatformDbName ??= p.ProposeName("paylink_", "platform");
            Tested = PlatformTarget is not null;
        }
        if (Step == "loginlink")
        {
            var firstSub = LoginLinkSteps.Select(s => s.Key).FirstOrDefault(k => !IsLLDone(k)) ?? "admins";
            var subIndex = Array.FindIndex(LoginLinkSteps, s => s.Key == LLStep);
            if (subIndex < 0 || subIndex > Array.FindIndex(LoginLinkSteps, s => s.Key == firstSub)) LLStep = firstSub;
            if (Request.Method == "GET")
            {
                LL.ServerUrl ??= Draft.Server ?? SavedLoginLink?.ServerUrl ?? configuration["PayLink:LoginLink:DefaultServer"]?.TrimEnd('/') ?? PublicLoginLink;
                LL.PublicBaseUrl ??= Draft.BaseUrl ?? await SafeBaseUrl() ?? DetectedBaseUrl;
                LL.PlatformApiClientId ??= Draft.PlatformId;
                LL.AppMode = Draft.OrgMode;
                LL.PlatformOrg ??= Draft.Org ?? SavedLoginLink?.PlatformOrg;
                LL.MgmtClientId ??= Draft.MgmtId;
                LL.ClientId ??= Draft.ClientId;
                LL.NewOrgName ??= Draft.NewOrgName;
                LL.NewOrgAdminName ??= Draft.NewOrgAdminName;
                LL.NewOrgAdminEmail ??= Draft.NewOrgAdminEmail;
                AdminAdmission = SavedLoginLink is null ? AdmissionModes.ApproveOnly : await settings.GetAdminAdmissionAsync();
                Tested = LLStep switch
                {
                    "server" => Draft.ServerOk,
                    "platform" => Draft.PlatformOk,
                    "org" => Draft.OrgOk,
                    _ => true,
                };
            }
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

    private Task SaveDraftAsync() => bootstrap.SetValueAsync(LoginLinkDraftKey, secrets.ProtectJson(Draft));

    public async Task OnGetAsync() => await LoadAsync();

    public async Task<IActionResult> OnPostUnlockAsync()
    {
        var code = await bootstrap.GetOrCreateSetupCodeAsync();
        if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes((Code ?? "").Trim().ToUpperInvariant()), Encoding.UTF8.GetBytes(code)))
        {
            await LoadAsync();
            Error = SetupCodeConfigured
                ? "That setup code doesn't match the one set in PayLink__SetupCode."
                : $"That setup code doesn't match. It's in PayLink's startup log and in {paths.SetupCodeFile}.";
            return Page();
        }
        Response.Cookies.Append(UnlockCookie, Protector.Protect(code), new CookieOptions
        {
            HttpOnly = true, Secure = Request.IsHttps, SameSite = SameSiteMode.Lax, MaxAge = TimeSpan.FromHours(12),
        });
        return Redirect("/setup?step=start");
    }

    /// <summary>A new installation: the person confirms they know the data folder holds the encryption key and must be backed up.</summary>
    public async Task<IActionResult> OnPostStartNewAsync(bool understood)
    {
        await LoadAsync();
        if (!Unlocked) return Redirect("/setup");
        Step = "start";
        if (!understood)
        {
            Error = "Tick the box to confirm you'll back up the data folder (it holds the key that makes stored passwords readable).";
            return Page();
        }
        await bootstrap.SetValueAsync(StartChoice, "new");
        return Redirect("/setup?step=hosting");
    }

    /// <summary>Restores a backup (.zip from Admin → Settings) or a bootstrap.db into the data folder.</summary>
    public async Task<IActionResult> OnPostRestoreAsync(IFormFile? backup)
    {
        await LoadAsync();
        if (!Unlocked) return Redirect("/setup");
        Step = "start";
        if (backup is null || backup.Length == 0) { Error = "Choose the backup file to upload."; return Page(); }
        RestoreResult result;
        await using (var stream = backup.OpenReadStream())
            result = await backups.RestoreAsync(stream, backup.FileName, HttpContext.RequestAborted);
        if (!result.Ok) { Error = result.Message; return Page(); }
        TempData["Ok"] = result.Message;
        if (result.SetupComplete) return Redirect("/admin/login");
        await bootstrap.SetValueAsync(StartChoice, "restored");
        // The restored bootstrap.db has its own setup code: stay unlocked with it.
        Response.Cookies.Append(UnlockCookie, Protector.Protect(await bootstrap.GetOrCreateSetupCodeAsync()), new CookieOptions
        {
            HttpOnly = true, Secure = Request.IsHttps, SameSite = SameSiteMode.Lax, MaxAge = TimeSpan.FromHours(12),
        });
        return Redirect("/setup");
    }

    public async Task<IActionResult> OnPostHostingAsync(string action)
    {
        await LoadAsync();
        if (!Unlocked) return Redirect("/setup");
        Step = "hosting";

        if (action == "skip" || (action == "save" && Hosting.Kind == HostingKind.None))
        {
            await bootstrap.SetHostingAsync(new HostingSettings());
            await bootstrap.SetHostingDraftAsync(null);
            await bootstrap.SetValueAsync(HostingDone, "true");
            return Redirect("/setup?step=database");
        }
        if (Hosting.Validate() is { } problem) { Error = problem; return Page(); }
        if (TenantAutoProvision is not (AutoProvisionModes.Off or AutoProvisionModes.Approved or AutoProvisionModes.Everyone))
            TenantAutoProvision = AutoProvisionModes.Approved;

        // Password fields come back empty after a test; fall back to what was just tested, then to what was saved.
        var next = Hosting.ToSettings(await bootstrap.GetHostingDraftAsync() ?? SavedHosting);
        if (action == "test")
        {
            var provisioner = DatabaseProvisioners.Create(next, httpFactory.CreateClient("hosting"));
            var check = provisioner is null ? new ProvisioningCheck(false, "Choose a hosting environment.") : await provisioner.TestAsync();
            await bootstrap.SetHostingDraftAsync(next);
            HostingDraftExists = true;
            HostingDraftOrSaved = next;
            Tested = check.Ok;
            if (check.Ok) Message = check.Message + " Nothing is saved yet: press Save and continue. Your secrets are kept, so you don't need to type them again.";
            else Error = check.Message;
            return Page();
        }

        // Saving tests again, so settings that can't create a database are never stored (a stored blank
        // password used to surface only later, as "Plesk refused the sign-in (401)" on the next step).
        if (DatabaseProvisioners.Create(next, httpFactory.CreateClient("hosting")) is { } tester)
        {
            var check = await tester.TestAsync();
            if (!check.Ok)
            {
                await bootstrap.SetHostingDraftAsync(next);
                HostingDraftExists = true;
                HostingDraftOrSaved = next;
                Error = "Not saved: " + check.Message;
                return Page();
            }
        }
        await bootstrap.SetHostingAsync(next);
        await bootstrap.SetHostingDraftAsync(null);
        await bootstrap.SetInitialTenantAutoProvisionAsync(TenantAutoProvision);
        await bootstrap.SetValueAsync(HostingDone, "true");
        return Redirect("/setup?step=database");
    }

    /// <summary>A server database typed as fields comes back with a blank password after a test: use the one that passed.</summary>
    private DatabaseTarget? FillFromDraft(DatabaseTarget? target)
    {
        if (target is null || DbDraft is null || DbDraft.Engine != target.Engine || target.Engine == DbEngine.Sqlite) return target;
        if (target.Engine == DbEngine.MySql)
        {
            var now = new MySqlConnector.MySqlConnectionStringBuilder(target.ConnectionString);
            var then = new MySqlConnector.MySqlConnectionStringBuilder(DbDraft.ConnectionString);
            if (now.Password.Length == 0 && now.Server == then.Server && now.Port == then.Port && now.Database == then.Database && now.UserID == then.UserID)
                return DbDraft;
        }
        else
        {
            var now = new Npgsql.NpgsqlConnectionStringBuilder(target.ConnectionString);
            var then = new Npgsql.NpgsqlConnectionStringBuilder(DbDraft.ConnectionString);
            if (string.IsNullOrEmpty(now.Password) && now.Host == then.Host && now.Port == then.Port && now.Database == then.Database && now.Username == then.Username)
                return DbDraft;
        }
        return target;
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
                if (Mysql.UseConnectionString && string.IsNullOrWhiteSpace(Mysql.ConnectionString) && DbDraft is not null && DbDraft.Engine != DbEngine.Sqlite)
                    target = DbDraft;   // the pasted string that just passed its test
                else if (!Mysql.TryBuild(out target, out var parseError)) { Error = parseError; return Page(); }
                target = FillFromDraft(target);
                break;
            case "mysql-create":
                var provisioner = DatabaseProvisioners.Create(SavedHosting, httpFactory.CreateClient("hosting"));
                if (provisioner is null) { Error = "Set up database hosting first, or choose another option."; return Page(); }
                if (action == "test")
                {
                    var c = await provisioner.TestAsync();
                    Tested = c.Ok;
                    if (c.Ok) Message = c.Message + " Press Create tables and continue to create the platform database.";
                    else Error = c.Message;
                    return Page();
                }
                var name = (PlatformDbName ?? "").Trim().ToLowerInvariant();
                if (name.Length == 0) name = provisioner.ProposeName("paylink_", "platform");
                try
                {
                    ProvisioningNames.EnsureSafeIdentifier(name, "database name");
                    created = await provisioner.CreateNamedAsync(name);
                }
                catch (ProvisioningException ex)
                {
                    Error = ex.Message + " Go back to Auto-provisioning to check the saved sign-in (it's tested when saved), or choose SQLite.";
                    return Page();
                }
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
            if (target!.Engine != DbEngine.Sqlite) await bootstrap.SetValueAsync(DbDraftKey, secrets.ProtectJson(target));
            Tested = true;
            Message = (existingVersion is null ? probe.Message : $"Connected. This is an existing PayLink platform database (schema {existingVersion}); setup will reattach to it.")
                      + " Nothing is created yet: press Create tables and continue.";
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
        await bootstrap.SetValueAsync(DbDraftKey, null);
        platformDb.Reset();
        // Carry the tenant auto-provisioning rule chosen in the previous step, unless an attached database already has one.
        if (await settings.GetAsync(PlatformSettings.KeyAutoProvisionMode) is null && await bootstrap.GetInitialTenantAutoProvisionAsync() is { } initialMode)
            await settings.SetAsync(PlatformSettings.KeyAutoProvisionMode, initialMode);
        await platformDb.AuditAsync(Actor.System, existingVersion is null ? "setup.platform_database_created" : "setup.platform_database_attached", target!.Describe());
        return Redirect("/setup?step=loginlink");
    }

    // ---------- LoginLink, step by step ----------

    private IActionResult LLPage(string sub)
    {
        Step = "loginlink";
        LLStep = sub;
        return Page();
    }

    /// <summary>5.1: the LoginLink server answers, and PayLink's public address is sound.</summary>
    public async Task<IActionResult> OnPostLLServerAsync(string action)
    {
        await LoadAsync();
        if (!Unlocked) return Redirect("/setup");
        if (PlatformTarget is null) return Redirect("/setup?step=database");
        if (action == "continue")
            return Draft.ServerOk ? Redirect("/setup?step=loginlink&ll=platform") : Fail("server", "Test the connection first.");

        var server = LoginLinkClient.NormalizeServer(LL.ServerUrl ?? "");
        var baseUrl = (LL.PublicBaseUrl ?? DetectedBaseUrl).Trim().TrimEnd('/');
        if (!Uri.TryCreate(server, UriKind.Absolute, out _)) return Fail("server", "Enter the LoginLink server address, e.g. https://loginlink.websvc.in");
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var b) || b.Scheme is not ("https" or "http")) return Fail("server", "Enter PayLink's public address, e.g. https://paylink.websvc.in");
        try
        {
            var config = await loginLink.GetDiscoveryAsync(LoginLinkClient.DiscoveryUrl(server, null));
            if (Draft.Server != server) Draft = new LoginLinkDraft();   // a different server: everything after this is tested again
            Draft.Server = server;
            Draft.BaseUrl = baseUrl;
            Draft.ServerOk = true;
            await SaveDraftAsync();
            Tested = true;
            Message = $"LoginLink answered at {config.Issuer}. People will come back to {baseUrl}/auth/callback after signing in. Nothing is saved in LoginLink yet.";
        }
        catch (LoginLinkException ex)
        {
            return Fail("server", $"LoginLink didn't answer at {server}: {ex.Message}");
        }
        return LLPage("server");
    }

    /// <summary>5.2: the platform credential works and really is a platform one (or the step is skipped).</summary>
    public async Task<IActionResult> OnPostLLPlatformAsync(string action)
    {
        await LoadAsync();
        if (!Unlocked || !Draft.ServerOk) return Redirect("/setup?step=loginlink");
        if (action == "skip")
        {
            Draft.PlatformId = Draft.PlatformSecret = null;
            Draft.PlatformOk = false;
            Draft.PlatformSkipped = true;
            if (Draft.OrgMode == "create-org") Draft.OrgOk = false;
            await SaveDraftAsync();
            return Redirect("/setup?step=loginlink&ll=org");
        }
        if (action == "continue")
            return Draft.PlatformOk ? Redirect("/setup?step=loginlink&ll=org") : Fail("platform", "Test the credential first, or skip this step.");

        var id = LL.PlatformApiClientId?.Trim();
        var secret = string.IsNullOrWhiteSpace(LL.PlatformApiClientSecret) && id == Draft.PlatformId ? Draft.PlatformSecret : LL.PlatformApiClientSecret?.Trim();
        if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(secret)) return Fail("platform", "Enter the platform credential's client ID and secret, or skip this step.");
        try
        {
            var root = await loginLink.GetDiscoveryAsync(LoginLinkClient.DiscoveryUrl(Draft.Server!, null));
            var token = await loginLink.ClientCredentialsTokenAsync(root.TokenEndpoint, id, secret);
            if (LoginLinkClient.TokenOrganization(token, Draft.Server!) is { Length: > 0 } owner)
                return Fail("platform", $"This credential belongs to the organization \"{owner}\", so it's an organization credential, not a platform one. Use it in the next sub-step (5.3), and create a platform credential in LoginLink at Admin → Platform Settings → Platform API Access.");
            Draft.PlatformId = id;
            Draft.PlatformSecret = secret;
            Draft.PlatformOk = true;
            Draft.PlatformSkipped = false;
            await SaveDraftAsync();
            Tested = true;
            Message = "The platform credential works. PayLink will be able to create LoginLink organizations, for its own team and for new tenants.";
        }
        catch (LoginLinkException ex)
        {
            return Fail("platform", "LoginLink didn't accept the platform credential: " + ex.Message);
        }
        return LLPage("platform");
    }

    /// <summary>5.3: the organization whose people run this PayLink, and how its "PayLink admin" app comes to be.</summary>
    public async Task<IActionResult> OnPostLLOrgAsync(string action)
    {
        await LoadAsync();
        if (!Unlocked || !Draft.ServerOk) return Redirect("/setup?step=loginlink");
        if (action == "continue")
            return Draft.OrgOk ? Redirect("/setup?step=loginlink&ll=admins") : Fail("org", "Test these details first.");

        var mode = LL.AppMode is "create-org" or "manual" or "root" ? LL.AppMode : "register";
        if (mode == "manual") LL.PlatformOrg = LL.ManualOrg;
        if (mode == "root") { LL.MgmtClientId = LL.RootMgmtClientId; LL.MgmtClientSecret = LL.RootMgmtClientSecret; }
        var org = mode is "create-org" or "root" ? "" : (LL.PlatformOrg ?? "").Trim().ToLowerInvariant();
        Draft.OrgOk = false;
        try
        {
            switch (mode)
            {
                case "create-org":
                    if (!Draft.PlatformOk) return Fail("org", "Creating an organization needs the platform credential (5.2). Go back and add it, or choose another option.");
                    var password = string.IsNullOrEmpty(LL.NewOrgAdminPassword) ? Draft.NewOrgAdminPassword : LL.NewOrgAdminPassword;
                    if (string.IsNullOrWhiteSpace(LL.NewOrgName) || string.IsNullOrWhiteSpace(LL.NewOrgAdminEmail) || !LL.NewOrgAdminEmail.Contains('@') || string.IsNullOrEmpty(password))
                        return Fail("org", "Enter the new organization's name, and your email and a password for its first admin.");
                    Draft.NewOrgName = LL.NewOrgName.Trim();
                    Draft.NewOrgAdminName = LL.NewOrgAdminName?.Trim();
                    Draft.NewOrgAdminEmail = LL.NewOrgAdminEmail.Trim();
                    Draft.NewOrgAdminPassword = password;
                    Message = $"Ready. When you finish, LoginLink creates the organization \"{Draft.NewOrgName}\" with the PayLink admin app, and you become its first admin.";
                    break;
                case "manual":
                    if (org.Length == 0) return Fail("org", "Enter the organization's slug.");
                    var clientSecret = string.IsNullOrWhiteSpace(LL.ClientSecret) && LL.ClientId?.Trim() == Draft.ClientId ? Draft.ClientSecret : LL.ClientSecret?.Trim();
                    if (string.IsNullOrWhiteSpace(LL.ClientId) || string.IsNullOrEmpty(clientSecret)) return Fail("org", "Enter the client ID and secret of the app you registered.");
                    await loginLink.GetDiscoveryAsync(LoginLinkClient.DiscoveryUrl(Draft.Server!, org));
                    Draft.ClientId = LL.ClientId.Trim();
                    Draft.ClientSecret = clientSecret;
                    Message = $"Found the organization \"{org}\". PayLink will use the app you registered.";
                    break;
                default:   // register in an existing organization, or in LoginLink's platform root
                    if (mode == "register" && org.Length == 0) return Fail("org", "Enter the organization's slug (it's in LoginLink's address bar, e.g. …/websvc/console).");
                    var mgmtId = LL.MgmtClientId?.Trim();
                    var mgmtSecret = string.IsNullOrWhiteSpace(LL.MgmtClientSecret) && mgmtId == Draft.MgmtId ? Draft.MgmtSecret : LL.MgmtClientSecret?.Trim();
                    if (string.IsNullOrEmpty(mgmtId) || string.IsNullOrEmpty(mgmtSecret))
                        return Fail("org", $"Enter a management credential of {(org.Length == 0 ? "LoginLink's platform root" : $"\"{org}\"")}: its Console → API Access, with Register apps.");
                    var config = await loginLink.GetDiscoveryAsync(LoginLinkClient.DiscoveryUrl(Draft.Server!, org));
                    var token = await loginLink.ClientCredentialsTokenAsync(config.TokenEndpoint, mgmtId, mgmtSecret);
                    if (LoginLinkClient.TokenOrganization(token, Draft.Server!) is { } owner && owner != org)
                        return Fail("org", owner.Length == 0
                            ? "This management credential belongs to LoginLink's platform root. Choose \"LoginLink's platform root\", or use a credential of the organization you entered."
                            : $"This management credential belongs to the organization \"{owner}\", not {(org.Length == 0 ? "the platform root" : $"\"{org}\"")}. Enter {owner} as the organization, or use a credential of {(org.Length == 0 ? "the platform root" : org)}.");
                    Draft.MgmtId = mgmtId;
                    Draft.MgmtSecret = mgmtSecret;
                    Message = $"The management credential works for {(org.Length == 0 ? "LoginLink's platform root" : $"\"{org}\"")}. When you finish, PayLink registers its admin app there and switches on a sign-in method for it.";
                    break;
            }
        }
        catch (LoginLinkException ex)
        {
            return Fail("org", ex.Message);
        }
        Draft.OrgMode = mode;
        Draft.Org = org;
        Draft.OrgOk = true;
        await SaveDraftAsync();
        Tested = true;
        return LLPage("org");
    }

    /// <summary>5.4: who may open /admin, then everything is created in LoginLink and saved.</summary>
    public async Task<IActionResult> OnPostLLAdminsAsync()
    {
        await LoadAsync();
        if (!Unlocked || !Draft.ServerOk || !Draft.OrgOk) return Redirect("/setup?step=loginlink");
        if (AdminAdmission is not (AdmissionModes.ApproveOnly or AdmissionModes.AutoTenantAdmins or AdmissionModes.Role)) AdminAdmission = AdmissionModes.ApproveOnly;
        var input = new LoginLinkInput
        {
            ServerUrl = Draft.Server, PublicBaseUrl = Draft.BaseUrl, PlatformOrg = Draft.Org,
            AdminScope = Draft.OrgMode == "root" ? "root" : "org",
            AppMode = Draft.OrgMode == "root" ? "register" : Draft.OrgMode,
            MgmtClientId = Draft.MgmtId, MgmtClientSecret = Draft.MgmtSecret, ClientId = Draft.ClientId, ClientSecret = Draft.ClientSecret,
            NewOrgName = Draft.NewOrgName, NewOrgAdminName = Draft.NewOrgAdminName, NewOrgAdminEmail = Draft.NewOrgAdminEmail, NewOrgAdminPassword = Draft.NewOrgAdminPassword,
            PlatformApiClientId = Draft.PlatformOk ? Draft.PlatformId : null, PlatformApiClientSecret = Draft.PlatformOk ? Draft.PlatformSecret : null,
        };
        var (error, note) = await ConnectAsync(input);
        if (error is not null) return Fail("admins", error);
        await settings.SetAsync(PlatformSettings.KeyAdminAdmission, AdminAdmission);
        await bootstrap.SetValueAsync(LoginLinkDraftKey, null);
        if (note is not null) TempData["SetupWarning"] = note;
        return Redirect("/setup?step=admin");
    }

    private IActionResult Fail(string sub, string error)
    {
        Error = error;
        return LLPage(sub);
    }

    /// <summary>The whole LoginLink step in one post (scripts and tests). The pages use the four sub-steps above.</summary>
    public async Task<IActionResult> OnPostLoginLinkAsync()
    {
        await LoadAsync();
        if (!Unlocked) return Redirect("/setup");
        if (PlatformTarget is null) return Redirect("/setup?step=database");
        Step = "loginlink";
        var (error, note) = await ConnectAsync(LL);
        if (error is not null) { Error = error; return Page(); }
        if (note is not null) TempData["SetupWarning"] = note;
        return Redirect("/setup?step=admin");
    }

    /// <summary>Checks everything, creates what's needed in LoginLink, and saves. Returns an error, or a note about the sign-in method.</summary>
    private async Task<(string? Error, string? Note)> ConnectAsync(LoginLinkInput ll)
    {
        var server = LoginLinkClient.NormalizeServer(ll.ServerUrl ?? "");
        var baseUrl = (ll.PublicBaseUrl ?? DetectedBaseUrl).Trim().TrimEnd('/');
        var org = ll.AdminScope == "root" ? "" : (ll.PlatformOrg ?? "").Trim().ToLowerInvariant();
        if (!Uri.TryCreate(server, UriKind.Absolute, out _)) return ("Enter the LoginLink server address, e.g. https://loginlink.websvc.in", null);
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out _)) return ("Enter PayLink's public address, e.g. https://paylink.websvc.in", null);
        if (ll.AdminScope != "root" && ll.AppMode != "create-org" && org.Length == 0)
            return ("Enter the slug of the organization whose people sign in to /admin (it's in LoginLink's address bar, e.g. /websvc), or choose LoginLink's platform root.", null);
        var hasPlatform = !string.IsNullOrWhiteSpace(ll.PlatformApiClientId) && !string.IsNullOrWhiteSpace(ll.PlatformApiClientSecret);

        try
        {
            var redirectUri = baseUrl + "/auth/callback";
            var rootConfig = await loginLink.GetDiscoveryAsync(LoginLinkClient.DiscoveryUrl(server, null));
            string? platformToken = null;
            if (hasPlatform)
            {
                platformToken = await loginLink.ClientCredentialsTokenAsync(rootConfig.TokenEndpoint, ll.PlatformApiClientId!.Trim(), ll.PlatformApiClientSecret!.Trim());
                if (LoginLinkClient.TokenOrganization(platformToken, server) is { Length: > 0 } owner)
                    return ($"The platform credential belongs to the organization \"{owner}\", so it's an organization credential, not a platform one. Create it in LoginLink at Admin → Platform Settings → Platform API Access.", null);
            }

            RegisteredApp app;
            string how;
            string? note = null;
            if (ll.AppMode == "create-org")
            {
                if (platformToken is null) return ("Creating an organization for PayLink's team needs the platform credential (CreateTenants).", null);
                if (string.IsNullOrWhiteSpace(ll.NewOrgName) || string.IsNullOrWhiteSpace(ll.NewOrgAdminEmail) || string.IsNullOrWhiteSpace(ll.NewOrgAdminPassword))
                    return ("Enter the new organization's name and its first admin's email and password.", null);
                var created = await loginLink.CreateOrganizationAsync(server, platformToken, ll.NewOrgName.Trim(), ll.NewOrgAdminEmail.Trim(),
                    ll.NewOrgAdminName?.Trim(), ll.NewOrgAdminPassword, "PayLink admin", redirectUri);
                if (created.App is null || string.IsNullOrEmpty(created.App.ClientId))
                    return ("LoginLink created the organization but didn't register the PayLink admin app. Register it there by hand and choose \"I registered it myself\".", null);
                app = created.App;
                org = created.Slug;
                how = $"organization {created.Slug} created with the platform credential";
            }
            else
            {
                await loginLink.GetDiscoveryAsync(LoginLinkClient.DiscoveryUrl(server, org));   // the organization must exist
                if (ll.AppMode == "manual")
                {
                    if (string.IsNullOrWhiteSpace(ll.ClientId) || string.IsNullOrWhiteSpace(ll.ClientSecret))
                        return ("Enter the client ID and secret of the app you registered in LoginLink.", null);
                    app = new RegisteredApp("", ll.ClientId.Trim(), ll.ClientSecret.Trim());
                    how = "app entered by hand";
                }
                else
                {
                    if (string.IsNullOrWhiteSpace(ll.MgmtClientId) || string.IsNullOrWhiteSpace(ll.MgmtClientSecret))
                        return (hasPlatform
                            ? "A platform credential can create organizations, but it can't register an app inside an existing one: LoginLink's Platform API only creates and manages organizations. Enter that organization's management credential (its Console → API Access, with Register apps), or choose \"Create a new organization for PayLink's team\"."
                            : $"Enter a management credential of {(org.Length == 0 ? "the platform root organization" : $"the organization \"{org}\"")} (its Console → API Access, with Register apps).", null);
                    var config = await loginLink.GetDiscoveryAsync(LoginLinkClient.DiscoveryUrl(server, org));
                    var token = await loginLink.ClientCredentialsTokenAsync(config.TokenEndpoint, ll.MgmtClientId.Trim(), ll.MgmtClientSecret.Trim());
                    // A management credential registers the app in its own organization, whatever slug was typed: they must match,
                    // or people of one organization would be sent to sign in at another (LoginLink's "issuer validation failed").
                    if (LoginLinkClient.TokenOrganization(token, server) is { } owner && owner != org)
                        return (owner.Length == 0
                            ? "This management credential belongs to LoginLink's platform root organization. Choose \"LoginLink's platform root\", or use a credential of the organization you entered."
                            : $"This management credential belongs to the organization \"{owner}\", but /admin is set to {(org.Length == 0 ? "the platform root" : $"\"{org}\"")}. Enter {owner} as the organization, or use a credential of {(org.Length == 0 ? "the platform root" : org)}.", null);
                    app = await loginLink.RegisterAppAsync(server, token, "PayLink admin", redirectUri);
                    note = app.SignInNote;
                    how = $"app {app.AppId} registered";
                }
            }

            var saved = new LoginLinkSettings
            {
                ServerUrl = server, PlatformOrg = org, AdminAppId = app.AppId, AdminClientId = app.ClientId, AdminClientSecret = app.ClientSecret,
            };
            if (hasPlatform)
            {
                saved.PlatformApiClientId = ll.PlatformApiClientId!.Trim();
                saved.PlatformApiClientSecret = ll.PlatformApiClientSecret!.Trim();
            }

            await settings.SetAsync(PlatformSettings.KeyPublicBaseUrl, baseUrl);
            await settings.SetLoginLinkAsync(saved);
            await platformDb.AuditAsync(Actor.System, "setup.loginlink_connected", string.IsNullOrEmpty(org) ? server : $"{server}/{org}", how);
            return (null, note);
        }
        catch (LoginLinkException ex)
        {
            return (ex.Message, null);
        }
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
