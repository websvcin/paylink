using System.Net;
using System.Security.Cryptography;
using Microsoft.Extensions.Configuration;
using PayLink.Infrastructure;
using PayLink.Platform;
using PayLink.Tests.Fakes;
using Xunit;
using static PayLink.Tests.Flows;

namespace PayLink.Tests;

/// <summary>Setup: new or restore, the encryption key, and the LoginLink step's checks.</summary>
public sealed class SetupTests
{
    private static readonly FakeLoginLink.Person Priya = new("sub-priya", "priya@acme.in", "Priya", "tenant_admin", []);

    private static async Task<Browser> UnlockAsync(PayLinkApp app)
    {
        var b = new Browser(app);
        var r = await b.SubmitAsync("/setup", "Unlock", F(("Code", await app.SetupCodeAsync())));
        Assert.Equal("/setup?step=start", r.Headers.Location?.OriginalString);
        return b;
    }

    [Fact]
    public async Task A_new_installation_needs_the_key_acknowledged()
    {
        await using var app = new PayLinkApp();
        var b = await UnlockAsync(app);
        var html = await b.TextAsync(await b.GetAsync("/setup?step=start"));
        Assert.Contains("fingerprint", html);
        Assert.Contains(app.Get<SecretProtector>().Fingerprint, html);

        var r = await b.SubmitAsync("/setup?step=start", "StartNew", F(("keyPlace", "bootstrap")));
        Assert.Contains("Tick the box", await b.TextAsync(r));
        r = await b.SubmitAsync("/setup?step=start", "StartNew", F(("keyPlace", "environment"), ("understood", "true")));
        Assert.Contains("PayLink__MasterKey isn&#x27;t set yet", await b.TextAsync(r));
        r = await b.SubmitAsync("/setup?step=start", "StartNew", F(("keyPlace", "bootstrap"), ("understood", "true")));
        Assert.Equal("/setup?step=hosting", r.Headers.Location?.OriginalString);

        // The key lives in bootstrap.db, not in a keys/ folder.
        Assert.False(File.Exists(Path.Combine(app.DataRoot, "keys", "master.key")));
        Assert.NotNull(SecretProtector.ReadKey(Path.Combine(app.DataRoot, "bootstrap.db")));
    }

    [Fact]
    public void A_master_key_file_from_an_earlier_version_is_imported()
    {
        var root = Path.Combine(Path.GetTempPath(), "paylink-key-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "keys"));
        try
        {
            var key = RandomNumberGenerator.GetBytes(32);
            File.WriteAllText(Path.Combine(root, "keys", "master.key"), Convert.ToBase64String(key));
            var secret = new SecretProtector(key).Protect("hello");

            var config = new Microsoft.Extensions.Configuration.ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["PayLink:DataRoot"] = root }).Build();
            var env = new FakeEnv(root);
            var protector = new SecretProtector(new AppPaths(config, env), config);
            Assert.Equal("hello", protector.Unprotect(secret));
            Assert.Equal(key, SecretProtector.ReadKey(Path.Combine(root, "bootstrap.db")));
            Assert.True(File.Exists(Path.Combine(root, "keys", "master.key.imported")));
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task A_backup_restores_on_a_new_server_and_everything_still_works()
    {
        await using var ll = await FakeLoginLink.StartAsync();
        byte[] zip;
        await using (var app = new PayLinkApp())
        {
            var admin = new Browser(app);
            await SetupAsync(app, admin, ll, database: SqlitePlatform);   // a backup is the SQLite files
            await CreateTenantAsync(app, ll, "acme", "Acme Learning", Priya, db: "sqlite");
            var download = await admin.GetAsync("/admin/settings?handler=Backup");
            Assert.Equal(HttpStatusCode.OK, download.StatusCode);
            Assert.Equal("application/zip", download.Content.Headers.ContentType?.MediaType);
            zip = await download.Content.ReadAsByteArrayAsync();
        }

        await using var fresh = new PayLinkApp();
        var b = await UnlockAsync(fresh);
        var r = await b.SubmitWithFileAsync("/setup?step=start", "Restore", F(), "backup", "notes.txt", "hello"u8.ToArray(), "text/plain");
        Assert.Contains("isn&#x27;t a PayLink backup", await b.TextAsync(r));

        r = await b.SubmitWithFileAsync("/setup?step=start", "Restore", F(), "backup", "paylink-backup.zip", zip, "application/zip");
        Assert.Equal("/admin/login", r.Headers.Location?.OriginalString);

        // The tenant, its SQLite database (now in the new data folder) and the stored LoginLink secrets all came along.
        var tenant = (await fresh.Get<PlatformRepository>().TenantBySlugAsync("acme"))!;
        Assert.StartsWith(fresh.DataRoot, fresh.Get<PayLink.Tenancy.TenantDb>().Target(tenant).SqliteFilePath);
        Assert.Equal(ll.BaseUrl, (await fresh.Get<PlatformSettings>().GetLoginLinkAsync())!.ServerUrl);
        var (_, console) = await ConsoleSignInAsync(new Browser(fresh), ll, "acme", "acme", Priya);
        Assert.Contains("Acme Learning", console);
        Assert.Equal(HttpStatusCode.Redirect, (await new Browser(fresh).GetAsync("/setup")).StatusCode);
    }

    [Fact]
    public async Task The_LoginLink_step_explains_which_credential_is_needed()
    {
        await using var ll = await FakeLoginLink.StartAsync();
        await using var app = new PayLinkApp();
        var b = await UnlockAsync(app);
        await b.SubmitAsync("/setup?step=start", "StartNew", F(("keyPlace", "bootstrap"), ("understood", "true")));
        await b.SubmitAsync("/setup?step=hosting", "Hosting", F(("action", "skip")));
        await b.SubmitAsync("/setup?step=database", "Database", F(("DbChoice", "sqlite"), ("action", "save")));
        var platform = ll.AddPlatformCredential();
        Dictionary<string, string?> Fields(params (string, string?)[] extra)
        {
            var f = F(("LL.ServerUrl", ll.BaseUrl), ("LL.PublicBaseUrl", "http://localhost"), ("LL.AdminScope", "org"), ("LL.AppMode", "register"));
            foreach (var (k, v) in extra) f[k] = v;
            return f;
        }

        // No slug: asked for one instead of silently using the platform root.
        var r = await b.SubmitAsync("/setup?step=loginlink", "LoginLink", Fields());
        Assert.Contains("Enter the slug of the organization", await b.TextAsync(r));

        // Only a platform credential: told why it can't register an app in an existing organization.
        r = await b.SubmitAsync("/setup?step=loginlink", "LoginLink", Fields(("LL.PlatformOrg", "websvc"),
            ("LL.PlatformApiClientId", platform.ClientId), ("LL.PlatformApiClientSecret", platform.Secret)));
        Assert.Contains("can&#x27;t register an app inside an existing one", await b.TextAsync(r));

        // A management credential of another organization than the one typed.
        var acme = ll.AddManagementCredential("acme");
        r = await b.SubmitAsync("/setup?step=loginlink", "LoginLink", Fields(("LL.PlatformOrg", "websvc"), ("LL.MgmtClientId", acme.ClientId), ("LL.MgmtClientSecret", acme.Secret)));
        Assert.Contains("belongs to the organization &quot;acme&quot;", await b.TextAsync(r));

        // Platform keys only: PayLink creates an organization for its team, and its owner becomes the first admin.
        r = await b.SubmitAsync("/setup?step=loginlink", "LoginLink", Fields(("LL.AppMode", "create-org"),
            ("LL.NewOrgName", "PayLink Team"), ("LL.NewOrgAdminEmail", "naren@websvc.in"), ("LL.NewOrgAdminName", "Naren"), ("LL.NewOrgAdminPassword", "a-strong-password"),
            ("LL.PlatformApiClientId", platform.ClientId), ("LL.PlatformApiClientSecret", platform.Secret)));
        Assert.True(r.Headers.Location?.OriginalString == "/setup?step=admin", await Problem(b, r));
        var saved = (await app.Get<PlatformSettings>().GetLoginLinkAsync())!;
        Assert.Equal("paylinkteam", saved.PlatformOrg);
        Assert.True(saved.CanCreateOrganizations);

        ll.NextSignIn("paylinkteam", ll.OwnerOf("paylinkteam"));
        r = await b.FollowAsync(await b.SubmitAsync("/setup?step=admin", "SignIn", F()));
        Assert.Contains("Setup is complete", await b.TextAsync(r));
    }

    [Fact]
    public async Task Saving_auto_provisioning_tests_it_first_and_refuses_a_wrong_password()
    {
        var adminCs = Environment.GetEnvironmentVariable("PAYLINK_TEST_MYSQL_ADMIN");
        if (string.IsNullOrEmpty(adminCs)) return;
        var admin = new MySqlConnector.MySqlConnectionStringBuilder(adminCs);
        await using var app = new PayLinkApp();
        var b = await UnlockAsync(app);
        await b.SubmitAsync("/setup?step=start", "StartNew", F(("keyPlace", "bootstrap"), ("understood", "true")));
        var r = await b.SubmitAsync("/setup?step=hosting", "Hosting", F(("action", "save"), ("Hosting.Kind", "MySqlServer"), ("Hosting.MySqlHost", admin.Server),
            ("Hosting.MySqlPort", (admin.Port == 0 ? 3306 : admin.Port).ToString()), ("Hosting.MySqlAdminUser", admin.UserID),
            ("Hosting.MySqlAdminPassword", "wrong-password"), ("Hosting.MySqlSslMode", "None")));
        Assert.Contains("Not saved:", await b.TextAsync(r));
        Assert.Equal(WebSvc.PayLink.Storage.Provisioning.HostingKind.None, (await app.Get<BootstrapStore>().GetHostingAsync()).Kind);
    }

    private sealed class FakeEnv(string root) : Microsoft.AspNetCore.Hosting.IWebHostEnvironment
    {
        public string WebRootPath { get; set; } = root;
        public Microsoft.Extensions.FileProviders.IFileProvider WebRootFileProvider { get; set; } = new Microsoft.Extensions.FileProviders.NullFileProvider();
        public string ApplicationName { get; set; } = "PayLink";
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = new Microsoft.Extensions.FileProviders.NullFileProvider();
        public string ContentRootPath { get; set; } = root;
        public string EnvironmentName { get; set; } = "Development";
    }
}
