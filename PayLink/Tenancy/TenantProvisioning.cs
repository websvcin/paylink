using System.Text.RegularExpressions;
using PayLink.Identity;
using PayLink.Infrastructure;
using PayLink.Platform;
using WebSvc.PayLink.Storage;
using WebSvc.PayLink.Storage.Provisioning;

namespace PayLink.Tenancy;

/// <summary>How a tenant's team signs in: a LoginLink organization with a "PayLink console" app registered in it.</summary>
public sealed class OrgConnection
{
    public string OrgSlug { get; set; } = "";
    public string Issuer { get; set; } = "";
    public string DiscoveryUrl { get; set; } = "";
    public string Mode { get; set; } = "existing";          // existing | created
    public string? AppId { get; set; }
    public string ClientId { get; set; } = "";
    public string ClientSecret { get; set; } = "";
    public string? MgmtClientId { get; set; }
    public string? MgmtClientSecret { get; set; }
    public string ApiRegistration { get; set; } = "skipped";
    public string? ApiRegistrationError { get; set; }
}

public enum DatabaseMode { Sqlite, Auto, Byo }

public sealed record DatabaseChoice(DatabaseMode Mode, DatabaseTarget? ByoTarget = null);

public sealed record NewOwner(string Subject, string? Email, string? Name);

public sealed record TenantCreateRequest(
    string Name,
    string Slug,
    OrgConnection Org,
    DatabaseChoice Database,
    NewOwner? Owner,
    string CreatedVia,
    Actor CreatedBy,
    long? CreatedByKeyId = null);

public sealed class TenantCreateException(string message) : Exception(message);

public static partial class Slugs
{
    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "admin", "api", "auth", "setup", "signup", "healthz", "docs", "css", "js", "lib", "img", "static", "assets",
        "console", "login", "logout", "pay", "hooks", "l", "www", "paylink", "platform", "support", "help", "status",
    };

    [GeneratedRegex("^[a-z0-9](?:[a-z0-9-]{0,38}[a-z0-9])?$")]
    private static partial Regex Pattern();

    /// <summary>Returns null when valid, otherwise a plain explanation.</summary>
    public static string? Problem(string? slug)
    {
        if (string.IsNullOrWhiteSpace(slug) || slug.Length < 2) return "Use at least 2 characters.";
        if (!Pattern().IsMatch(slug)) return "Use lowercase letters, digits and hyphens (2–40 characters), starting and ending with a letter or digit.";
        if (Reserved.Contains(slug)) return $"\"{slug}\" is reserved. Choose another.";
        return null;
    }

    [GeneratedRegex("[^a-z0-9]+")]
    private static partial Regex NonAlnum();

    public static string Suggest(string name)
    {
        var s = NonAlnum().Replace(name.ToLowerInvariant(), "-").Trim('-');
        return s.Length > 40 ? s[..40].Trim('-') : s;
    }
}

/// <summary>Connects or creates a tenant's LoginLink organization.</summary>
public sealed class OrgConnector(LoginLinkClient loginLink, PlatformSettings settings, PlatformRepository platform)
{
    private const string ConsoleAppName = "PayLink console";

    public async Task<string> CallbackUrlAsync() => await settings.GetPublicBaseUrlAsync() + "/auth/callback";

    /// <summary>
    /// Connects an organization the tenant already has, using a LoginLink management credential with RegisterApps
    /// (and ManageApis to register PayLink as an API). Registers the "PayLink console" app in that organization.
    /// </summary>
    public async Task<OrgConnection> ConnectExistingAsync(string orgSlug, string mgmtClientId, string mgmtClientSecret, CancellationToken ct = default)
    {
        orgSlug = orgSlug.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(orgSlug)) throw new TenantCreateException("Enter the LoginLink organization slug.");
        if (await platform.OrgInUseAsync(orgSlug))
            throw new TenantCreateException($"The LoginLink organization \"{orgSlug}\" is already connected to a PayLink tenant. One organization can serve one tenant.");

        var ll = await settings.GetLoginLinkAsync() ?? throw new TenantCreateException("LoginLink isn't set up on this platform yet.");
        var discoveryUrl = LoginLinkClient.DiscoveryUrl(ll.ServerUrl, orgSlug);
        var config = await loginLink.GetDiscoveryAsync(discoveryUrl, ct);
        var token = await loginLink.ClientCredentialsTokenAsync(config.TokenEndpoint, mgmtClientId, mgmtClientSecret, ct);
        var app = await loginLink.RegisterAppAsync(ll.ServerUrl, token, ConsoleAppName, await CallbackUrlAsync(), ct);

        var connection = new OrgConnection
        {
            OrgSlug = orgSlug, Issuer = config.Issuer, DiscoveryUrl = discoveryUrl, Mode = "existing",
            AppId = app.AppId, ClientId = app.ClientId, ClientSecret = app.ClientSecret,
            MgmtClientId = mgmtClientId, MgmtClientSecret = mgmtClientSecret,
        };
        try
        {
            await loginLink.EnsureApiAsync(ll.ServerUrl, token, await settings.GetPublicBaseUrlAsync(), ct);
            connection.ApiRegistration = "ok";
        }
        catch (LoginLinkException ex)
        {
            // Not needed until the Payments API accepts LoginLink tokens; recorded so it can be retried.
            connection.ApiRegistration = "failed";
            connection.ApiRegistrationError = ex.Message;
        }
        return connection;
    }

    /// <summary>Creates a brand-new LoginLink organization through LoginLink's Platform API (CreateTenants).</summary>
    public async Task<OrgConnection> CreateNewAsync(string tenantName, string adminEmail, string? adminName, string password, CancellationToken ct = default)
    {
        var ll = await settings.GetLoginLinkAsync() ?? throw new TenantCreateException("LoginLink isn't set up on this platform yet.");
        if (!ll.CanCreateOrganizations)
            throw new TenantCreateException("This platform can't create LoginLink organizations. Connect an existing organization instead.");

        var root = await loginLink.GetDiscoveryAsync(LoginLinkClient.DiscoveryUrl(ll.ServerUrl, null), ct);
        var token = await loginLink.ClientCredentialsTokenAsync(root.TokenEndpoint, ll.PlatformApiClientId!, ll.PlatformApiClientSecret!, ct);
        var created = await loginLink.CreateOrganizationAsync(ll.ServerUrl, token, tenantName, adminEmail, adminName, password,
            ConsoleAppName, await CallbackUrlAsync(), ct);
        if (created.App is null || string.IsNullOrEmpty(created.App.ClientId))
            throw new TenantCreateException("LoginLink created the organization but didn't register the PayLink console app.");

        return new OrgConnection
        {
            OrgSlug = created.Slug, Issuer = created.Issuer, DiscoveryUrl = created.DiscoveryUrl, Mode = "created",
            AppId = created.App.AppId, ClientId = created.App.ClientId, ClientSecret = created.App.ClientSecret,
            ApiRegistration = "skipped", ApiRegistrationError = "A new organization has no management credential yet.",
        };
    }
}

/// <summary>Creates a tenant: its database (SQLite, auto-provisioned MySQL or the tenant's own MySQL), schema, owner and registry entry.</summary>
public sealed class TenantProvisioning(
    PlatformRepository platform,
    PlatformSettings settings,
    PlatformDb platformDb,
    TenantDb tenantDb,
    BootstrapStore bootstrap,
    SecretProtector secrets,
    AppPaths paths,
    IHttpClientFactory httpFactory,
    ILogger<TenantProvisioning> logger)
{
    /// <summary>Whether this organization may have a MySQL database created for it, and why not if it may not.</summary>
    public async Task<(bool Allowed, string Reason)> CanAutoProvisionAsync(string orgSlug)
    {
        var hosting = await bootstrap.GetHostingAsync();
        if (hosting.Kind == HostingKind.None)
            return (false, "This platform has no database hosting set up.");
        var mode = await settings.GetAutoProvisionModeAsync();
        return mode switch
        {
            AutoProvisionModes.Everyone => (true, ""),
            AutoProvisionModes.Approved when await platform.IsApprovedAsync(orgSlug) => (true, ""),
            AutoProvisionModes.Approved => (false, $"Not available for \"{orgSlug}\". Ask your platform administrator to approve this LoginLink organization, or start on SQLite and move later."),
            _ => (false, "Automatic database creation is turned off on this platform."),
        };
    }

    /// <summary>Checks the name and address before anything is created in LoginLink. Returns null when both are fine.</summary>
    public async Task<string?> CheckNameAndSlugAsync(string? name, string? slug)
    {
        if (string.IsNullOrWhiteSpace(name)) return "Enter a business name.";
        var s = (slug ?? "").Trim().ToLowerInvariant();
        if (Slugs.Problem(s) is { } problem) return problem;
        if (await platform.TenantBySlugAsync(s) is not null) return $"\"{s}\" is already taken. Choose another address.";
        return null;
    }

    /// <param name="bypassApprovalList">Platform admins may auto-provision for any organization; the approval list only limits self-service signup.</param>
    public async Task<TenantRecord> CreateAsync(TenantCreateRequest request, bool bypassApprovalList = false, CancellationToken ct = default)
    {
        var slug = request.Slug.Trim().ToLowerInvariant();
        if (Slugs.Problem(slug) is { } problem) throw new TenantCreateException(problem);
        if (string.IsNullOrWhiteSpace(request.Name)) throw new TenantCreateException("Enter a business name.");
        if (await platform.TenantBySlugAsync(slug) is not null) throw new TenantCreateException($"\"{slug}\" is already taken. Choose another address.");
        if (await platform.OrgInUseAsync(request.Org.OrgSlug))
            throw new TenantCreateException($"The LoginLink organization \"{request.Org.OrgSlug}\" is already connected to a PayLink tenant.");

        DatabaseTarget target;
        string? providerRef = null;
        IDatabaseProvisioner? provisioner = null;
        ProvisionedDatabase? provisioned = null;

        switch (request.Database.Mode)
        {
            case DatabaseMode.Sqlite:
                Directory.CreateDirectory(paths.TenantsDirectory);
                target = DatabaseTarget.ForSqliteFile(Path.Combine(paths.TenantsDirectory, $"tenant_{slug}_{ProvisioningNames.RandomToken(4)}.db"));
                break;

            case DatabaseMode.Auto:
                var (allowed, reason) = await CanAutoProvisionAsync(request.Org.OrgSlug);
                if (!allowed && !(bypassApprovalList && (await bootstrap.GetHostingAsync()).Kind != HostingKind.None))
                    throw new TenantCreateException(reason);
                provisioner = DatabaseProvisioners.Create(await bootstrap.GetHostingAsync(), httpFactory.CreateClient("hosting"))
                    ?? throw new TenantCreateException("Database hosting isn't set up.");
                try
                {
                    provisioned = await provisioner.CreateAsync("paylink_", slug, ct);
                }
                catch (ProvisioningException ex)
                {
                    throw new TenantCreateException("The hosting environment couldn't create the database: " + ex.Message);
                }
                target = provisioned.Target;
                providerRef = provisioned.ProviderReference + "|" + provisioned.DatabaseName + "|" + provisioned.UserName;
                break;

            case DatabaseMode.Byo:
                target = request.Database.ByoTarget ?? throw new TenantCreateException("Enter your MySQL connection details.");
                var probe = await DatabaseProbe.TestAsync(target, ct);
                if (!probe.Ok) throw new TenantCreateException(probe.Message);
                if (!probe.IsEmpty) throw new TenantCreateException($"Your database must be empty, but it already has {probe.TableCount} table(s). Create a new, empty database for PayLink.");
                if (!probe.CanCreateTables) throw new TenantCreateException(probe.Message);
                break;

            default:
                throw new TenantCreateException("Choose where the data lives.");
        }

        try
        {
            await tenantDb.MigrateAsync(target, slug);

            var record = new TenantRecord
            {
                Slug = slug, Name = request.Name.Trim(), Status = "active",
                DbMode = request.Database.Mode.ToString().ToLowerInvariant(), DbEngine = target.Engine.ToString(),
                DbDisplay = target.Describe(), DbConnectionEnc = secrets.ProtectJson(target),
                DbProviderRefEnc = secrets.ProtectOrNull(providerRef is null ? null : $"{provisioner!.Kind}|{providerRef}"),
                LoginlinkOrg = request.Org.OrgSlug, LoginlinkIssuer = request.Org.Issuer, LoginlinkMode = request.Org.Mode,
                ConsoleAppId = request.Org.AppId, ConsoleClientId = request.Org.ClientId,
                ConsoleClientSecretEnc = secrets.Protect(request.Org.ClientSecret),
                MgmtClientId = request.Org.MgmtClientId, MgmtClientSecretEnc = secrets.ProtectOrNull(request.Org.MgmtClientSecret),
                ApiRegistration = request.Org.ApiRegistration, ApiRegistrationError = request.Org.ApiRegistrationError,
                CreatedAt = Clock.Now(), CreatedBy = request.CreatedBy.Name, CreatedVia = request.CreatedVia, CreatedByKeyId = request.CreatedByKeyId,
            };

            await using (var c = await Db.OpenAsync(target, ct))
                await AuditLog.WriteAsync(c, request.CreatedBy, "tenant.created", slug, $"database: {record.DbDisplay}");

            record.Id = await platform.InsertTenantAsync(record);

            if (request.Owner is { } o)
            {
                await tenantDb.UpsertMemberAsync(record, new Member
                {
                    Subject = o.Subject, Email = o.Email, Name = o.Name, Role = TenantRoles.Owner, Status = "approved",
                    Source = "signup", CreatedAt = Clock.Now(), DecidedBy = "signup", DecidedAt = Clock.Now(), LastSignInAt = Clock.Now(),
                });
            }

            if (request.Database.Mode == DatabaseMode.Auto) await platform.MarkApprovalUsedAsync(request.Org.OrgSlug);
            await platformDb.AuditAsync(request.CreatedBy, "tenant.created", slug,
                $"via {request.CreatedVia}; LoginLink {request.Org.Mode} organization {request.Org.OrgSlug}; database {record.DbDisplay}");
            logger.LogInformation("Tenant {Slug} created ({Database})", slug, record.DbDisplay);
            return record;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Creating tenant {Slug} failed; cleaning up", slug);
            await CleanUpAsync(request.Database.Mode, target, provisioner, provisioned);
            throw ex is TenantCreateException ? ex : new TenantCreateException("Creating the tenant failed: " + ex.Message);
        }
    }

    private static async Task CleanUpAsync(DatabaseMode mode, DatabaseTarget target, IDatabaseProvisioner? provisioner, ProvisionedDatabase? provisioned)
    {
        try
        {
            if (mode == DatabaseMode.Sqlite && target.SqliteFilePath is { } file)
            {
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                foreach (var f in new[] { file, file + "-wal", file + "-shm" }) if (File.Exists(f)) File.Delete(f);
            }
            else if (mode == DatabaseMode.Auto && provisioner is not null && provisioned is not null)
            {
                await provisioner.DropAsync(provisioned);
            }
            // A tenant's own (BYO) database is never dropped; it was empty before and PayLink only added tables to it.
        }
        catch (Exception)
        {
            // Best effort: the original error is what the person needs to see.
        }
    }
}
