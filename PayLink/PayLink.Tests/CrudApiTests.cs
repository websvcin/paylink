using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using MySqlConnector;
using PayLink.Platform;
using PayLink.Tenancy;
using PayLink.Tests.Fakes;
using WebSvc.PayLink.Storage;
using Xunit;
using static PayLink.Tests.Flows;

namespace PayLink.Tests;

/// <summary>Phase 4: Data access → Connect CrudApi (a read-only user, registration in a CrudApi workspace, disconnect).</summary>
public sealed class CrudApiTests
{
    private static readonly FakeLoginLink.Person Delta = new("sub-d", "d@delta.in", "D", "tenant_admin", []);

    /// <summary>CrudApi's workspace developer API, as much as the journey uses.</summary>
    private sealed class FakeCrudApi : HttpMessageHandler
    {
        public List<(string Method, string Path, string Body)> Calls { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
            Calls.Add((request.Method.Method, request.RequestUri!.AbsolutePath, body));
            if (request.Headers.Authorization?.Parameter != "wdk_good") return Json(HttpStatusCode.Unauthorized, """{"error":"invalid_key"}""");
            return (request.Method.Method, request.RequestUri.AbsolutePath) switch
            {
                ("GET", "/api/tenant/databases") => Json(HttpStatusCode.OK, "[]"),
                ("POST", "/api/tenant/databases") => Json(HttpStatusCode.OK, """{"id":"db_7"}"""),
                ("POST", "/api/tenant/api-clients") => Json(HttpStatusCode.OK, """{"clientId":"cli_1","clientSecret":"sec_reader"}"""),
                ("DELETE", "/api/tenant/databases/db_7") => Json(HttpStatusCode.NoContent, ""),
                _ => Json(HttpStatusCode.NotFound, "{}"),
            };
        }
        private static HttpResponseMessage Json(HttpStatusCode s, string text) => new(s) { Content = new StringContent(text, Encoding.UTF8, "application/json") };
    }

    [Fact]
    public async Task SQLite_tenants_are_told_why_and_what_to_do()
    {
        await using var ll = await FakeLoginLink.StartAsync();
        await using var app = new PayLinkApp();
        await SetupAsync(app, new Browser(app), ll);
        var owner = await CreateTenantAsync(app, ll, "acme", "Acme Learning", Delta, db: "sqlite");
        var html = await owner.TextAsync(await owner.GetAsync("/acme/console/data"));
        Assert.Contains("CrudApi reads MySQL databases", html);
        Assert.Contains("/api/v1/reporting/payments", html);
    }

    /// <summary>Runs only when PAYLINK_TEST_MYSQL_ADMIN is set.</summary>
    [Fact]
    public async Task An_auto_provisioned_tenant_connects_CrudApi_read_only()
    {
        var adminCs = Environment.GetEnvironmentVariable("PAYLINK_TEST_MYSQL_ADMIN");
        if (string.IsNullOrEmpty(adminCs)) return;
        var admin = new MySqlConnectionStringBuilder(adminCs);
        var crud = new FakeCrudApi();
        var created = new List<DatabaseTarget>();
        await using var ll = await FakeLoginLink.StartAsync();
        await using var app = new PayLinkApp(services: s => s.AddHttpClient(CrudApiClient.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => crud));
        try
        {
            var platformAdmin = new Browser(app);
            await SetupAsync(app, platformAdmin, ll, F(("action", "save"), ("Hosting.Kind", "MySqlServer"), ("Hosting.MySqlHost", admin.Server),
                ("Hosting.MySqlPort", (admin.Port == 0 ? 3306 : admin.Port).ToString()), ("Hosting.MySqlAdminUser", admin.UserID),
                ("Hosting.MySqlAdminPassword", admin.Password), ("Hosting.MySqlSslMode", "None"), ("Hosting.MySqlUserHost", "%")), SqlitePlatform);
            await platformAdmin.SubmitAsync("/admin/auto-provisioning", "Add", F(("orgSlug", "delta"), ("note", "pilot")));
            var delta = await CreateTenantAsync(app, ll, "delta", "Delta Foods", Delta, db: "auto");
            var tenant = (await app.Get<PlatformRepository>().TenantBySlugAsync("delta"))!;
            var target = app.Get<TenantDb>().Target(tenant);
            created.Add(target);
            var owner = new MySqlConnectionStringBuilder(target.ConnectionString);

            // The tenant's own database user can write, so it's refused.
            var r = await delta.FollowAsync(await delta.SubmitAsync("/delta/console/data", "Reader", F(("user", owner.UserID), ("password", owner.Password))));
            Assert.Contains("can change payments", await delta.TextAsync(r));

            // Blank: PayLink makes a SELECT-only user and checks it.
            r = await delta.FollowAsync(await delta.SubmitAsync("/delta/console/data", "Reader", F(("user", ""), ("host", "db.internal"))));
            Assert.Contains("Read-only user checked", await delta.TextAsync(r));
            var link = (await app.Get<CrudApiConnection>().GetAsync(tenant))!;
            Assert.Equal(("db.internal", owner.Database), (link.Host, link.Database));
            var reader = new MySqlConnectionStringBuilder(target.ConnectionString) { UserID = link.ReadUser, Password = link.ReadPassword };
            await using (var c = new MySqlConnection(reader.ToString()))
            {
                await c.OpenAsync();
                Assert.Equal(0L, await c.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM payments;"));
                await Assert.ThrowsAsync<MySqlException>(() => c.ExecuteAsync("DELETE FROM payments;"));
            }

            // A wrong key is refused; the right one registers the database with the read-only user and makes a Reader client.
            r = await delta.FollowAsync(await delta.SubmitAsync("/delta/console/data", "Register", F(("server", "https://crud.example.in"), ("workspace", "delta"), ("key", "wdk_bad"))));
            Assert.Contains("CrudApi didn&#x27;t accept the developer key", await delta.TextAsync(r));
            r = await delta.FollowAsync(await delta.SubmitAsync("/delta/console/data", "Register", F(("server", "https://crud.example.in/"), ("workspace", "delta"), ("key", "wdk_good"))));
            var html = await delta.TextAsync(r);
            Assert.Contains("sec_reader", html);
            Assert.Contains("https://crud.example.in/api/v1/delta/db_7/crud/payments", html);
            var registration = JsonNode.Parse(crud.Calls.Single(c => c.Method == "POST" && c.Path == "/api/tenant/databases").Body)!;
            Assert.Equal((link.ReadUser, "db.internal", owner.Database), ((string)registration["user"]!, (string)registration["host"]!, (string)registration["database"]!));
            Assert.Equal("Reader", (string)JsonNode.Parse(crud.Calls.Single(c => c.Path == "/api/tenant/api-clients").Body)!["role"]!);

            // Disconnect: unregistered in CrudApi, and the read-only user is gone.
            r = await delta.FollowAsync(await delta.SubmitAsync("/delta/console/data", "Disconnect", F()));
            Assert.Contains("Disconnected.", await delta.TextAsync(r));
            Assert.Contains(crud.Calls, c => c.Method == "DELETE" && c.Path == "/api/tenant/databases/db_7");
            Assert.Null(await app.Get<CrudApiConnection>().GetAsync(tenant));
            await MySqlConnection.ClearAllPoolsAsync();
            await using (var a = new MySqlConnection(adminCs))
                Assert.Equal(0L, await a.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM mysql.user WHERE user = @u;", new { u = link.ReadUser }));
        }
        finally
        {
            await using var a = new MySqlConnection(adminCs);
            foreach (var t in created)
            {
                var b = new MySqlConnectionStringBuilder(t.ConnectionString);
                await a.ExecuteAsync($"DROP DATABASE IF EXISTS `{b.Database}`;");
                await a.ExecuteAsync($"DROP USER IF EXISTS '{b.UserID}'@'%';");
            }
        }
    }
}
