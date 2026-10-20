using System.Net;
using System.Text.Json.Nodes;
using Dapper;
using MySqlConnector;
using PayLink.Infrastructure;
using PayLink.Platform;
using PayLink.Tenancy;
using PayLink.Tests.Fakes;
using WebSvc.PayLink.Storage;
using WebSvc.PayLink.Storage.Provisioning;
using Xunit;
using static PayLink.Tests.Flows;

namespace PayLink.Tests;

/// <summary>
/// The same journey against a real MySQL server: hosting, a platform database created through it, an auto-provisioned
/// tenant, a bring-your-own tenant, and a restart. Runs only when PAYLINK_TEST_MYSQL_ADMIN is set,
/// e.g. "Server=127.0.0.1;Port=3306;Uid=root;Pwd=…". Everything it creates is dropped at the end.
/// </summary>
public sealed class MySqlFlowTests
{
    private static string? Admin => Environment.GetEnvironmentVariable("PAYLINK_TEST_MYSQL_ADMIN");

    [Fact]
    public async Task Platform_and_tenants_on_mysql()
    {
        if (string.IsNullOrEmpty(Admin)) return;
        var admin = new MySqlConnectionStringBuilder(Admin);
        var hosting = F(("action", "save"), ("Hosting.Kind", "MySqlServer"), ("Hosting.MySqlHost", admin.Server),
            ("Hosting.MySqlPort", (admin.Port == 0 ? 3306 : admin.Port).ToString()), ("Hosting.MySqlAdminUser", admin.UserID),
            ("Hosting.MySqlAdminPassword", admin.Password), ("Hosting.MySqlSslMode", "None"), ("Hosting.MySqlUserHost", "%"));

        var created = new List<DatabaseTarget>();
        var provisioner = new MySqlServerProvisioner(new MySqlServerHosting
        {
            Host = admin.Server, Port = admin.Port == 0 ? 3306 : admin.Port, AdminUser = admin.UserID, AdminPassword = admin.Password,
            SslMode = MySqlSslMode.None,
        });
        var dataRoot = Path.Combine(Path.GetTempPath(), "paylink-mysql-" + Guid.NewGuid().ToString("N"));

        await using var ll = await FakeLoginLink.StartAsync();
        try
        {
            await using (var app = new PayLinkApp(dataRoot, keepData: true))
            {
                var platformAdmin = new Browser(app);
                // "Test & verify" first, then "Save" with the password fields blank, as a browser re-renders them:
                // the tested secrets must be kept (this used to store a blank password, so Plesk answered 401 later).
                var unlocked = await platformAdmin.SubmitAsync("/setup", "Unlock", F(("Code", await app.SetupCodeAsync())));
                Assert.Equal("/setup?step=hosting", unlocked.Headers.Location?.OriginalString);
                var tested = await platformAdmin.FollowAsync(await platformAdmin.SubmitAsync("/setup?step=hosting", "Hosting",
                    new Dictionary<string, string?>(hosting) { ["action"] = "test" }));
                Assert.Contains("Your secrets are kept", await platformAdmin.TextAsync(tested));
                var blank = new Dictionary<string, string?>(hosting) { ["Hosting.MySqlAdminPassword"] = "" };

                // A custom, full database name for the platform database.
                var platformName = "paylink_platform_t" + Guid.NewGuid().ToString("N")[..8];
                await SetupAsync(app, platformAdmin, ll, blank, F(("DbChoice", "mysql-create"), ("PlatformDbName", platformName), ("action", "save")));

                var platformTarget = (await app.Get<BootstrapStore>().GetPlatformDatabaseAsync())!;
                created.Add(platformTarget);
                Assert.Equal(DbEngine.MySql, platformTarget.Engine);
                Assert.StartsWith("MySQL", platformTarget.Describe());
                Assert.Equal(platformName, new MySqlConnectionStringBuilder(platformTarget.ConnectionString).Database);

                // Approve "delta" for auto-provisioning, then Delta signs up and gets its own MySQL database.
                var r = await platformAdmin.SubmitAsync("/admin/auto-provisioning", "Add", F(("orgSlug", "delta"), ("note", "pilot")));
                Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);

                var delta = new Browser(app);
                var html = await ConnectExistingAsync(delta, ll, "delta", new("sub-d", "d@delta.in", "D", "tenant_admin", []));
                Assert.Contains("value=\"auto\"", html);
                r = await delta.FollowAsync(await delta.SubmitAsync("/signup", "Create",
                    F(("TenantName", "Delta Foods"), ("TenantSlug", "delta"), ("DbChoice", "auto"), ("action", "create"))));
                Assert.True(delta.LastUrl!.AbsolutePath == "/delta/console", await Problem(delta, r));

                var deltaTenant = (await app.Get<PlatformRepository>().TenantBySlugAsync("delta"))!;
                var deltaTarget = app.Get<TenantDb>().Target(deltaTenant);
                created.Add(deltaTarget);
                Assert.Equal("auto", deltaTenant.DbMode);
                Assert.Equal(DbEngine.MySql, deltaTarget.Engine);
                Assert.NotNull((await app.Get<PlatformRepository>().ApprovalsAsync()).Single(a => a.OrgSlug == "delta").UsedAt);

                // Omega isn't approved: auto is greyed out, but it can bring its own (empty) MySQL database.
                var omegaDb = await provisioner.CreateAsync("paylink_", "omega_byo");
                created.Add(omegaDb.Target);
                var omega = new Browser(app);
                html = await ConnectExistingAsync(omega, ll, "omega", new("sub-o", "o@omega.in", "O", "tenant_admin", []));
                Assert.DoesNotContain("value=\"auto\"", html);
                Assert.Contains("Ask your platform administrator to approve this LoginLink organization", html.Replace("&quot;", "\""));

                // A non-empty database is refused, and nothing is created.
                await using (var c = await Db.OpenAsync(omegaDb.Target)) await c.ExecuteAsync("CREATE TABLE theirs (id INT);");
                var byoFields = F(("TenantName", "Omega"), ("TenantSlug", "omega"), ("DbChoice", "byo"), ("action", "create"),
                    ("Byo.UseConnectionString", "true"), ("Byo.ConnectionString", omegaDb.Target.ConnectionString));
                r = await omega.SubmitAsync("/signup", "Create", byoFields);
                Assert.Contains("must be empty", await omega.TextAsync(r));
                Assert.Null(await app.Get<PlatformRepository>().TenantBySlugAsync("omega"));

                await using (var c = await Db.OpenAsync(omegaDb.Target)) await c.ExecuteAsync("DROP TABLE theirs;");
                r = await omega.FollowAsync(await omega.SubmitAsync("/signup", "Create", byoFields));
                Assert.True(omega.LastUrl!.AbsolutePath == "/omega/console", await Problem(omega, r));
                var omegaTenant = (await app.Get<PlatformRepository>().TenantBySlugAsync("omega"))!;
                Assert.Equal("byo", omegaTenant.DbMode);

                // The tenant tables really are in the tenant's own databases.
                foreach (var target in new[] { deltaTarget, omegaDb.Target })
                {
                    await using var c = await Db.OpenAsync(target);
                    Assert.Equal(1, await c.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM members WHERE role = 'owner';"));
                    Assert.Equal("0001", await c.ExecuteScalarAsync<string>("SELECT MAX(version) FROM schema_migrations;"));
                }
                await using (var c = await app.Get<PlatformDb>().OpenAsync()) Assert.Null(await AuditLog.VerifyAsync(c));
            }

            // Restart on the same data folder: PayLink reattaches to MySQL and every tenant still works.
            await using (var app = new PayLinkApp(dataRoot, keepData: true))
            {
                var b = new Browser(app);
                var health = (await (await b.GetAsync("/healthz")).Content.ReadFromJsonAsync<JsonObject>())!;
                Assert.Equal("ok", (string?)health["status"]);
                Assert.True((bool)health["setupComplete"]!);
                var (_, console) = await ConsoleSignInAsync(b, ll, "delta", "delta", new("sub-d", "d@delta.in", "D", "tenant_admin", []));
                Assert.Contains("Delta Foods", console);
            }
        }
        finally
        {
            await MySqlConnection.ClearAllPoolsAsync();
            await using var c = new MySqlConnection(Admin);
            await c.OpenAsync();
            foreach (var t in created)
            {
                var b = new MySqlConnectionStringBuilder(t.ConnectionString);
                await c.ExecuteAsync($"DROP DATABASE IF EXISTS `{b.Database}`;");
                await c.ExecuteAsync($"DROP USER IF EXISTS '{b.UserID}'@'%';");
            }
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { Directory.Delete(dataRoot, true); } catch (IOException) { }
        }
    }
}

internal static class HttpContentJson
{
    public static Task<T?> ReadFromJsonAsync<T>(this HttpContent content) => System.Net.Http.Json.HttpContentJsonExtensions.ReadFromJsonAsync<T>(content);
}
