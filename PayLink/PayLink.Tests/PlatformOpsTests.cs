using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.Loader;
using System.Text.Json.Nodes;
using Dapper;
using PayLink.Payments;
using PayLink.Platform;
using PayLink.Tenancy;
using PayLink.Tests.Fakes;
using Xunit;
using static PayLink.Tests.Flows;

namespace PayLink.Tests;

/// <summary>Phase 3, platform operations: the connector catalog and runtime packages, limits, subdomains, moving databases, the dashboard.</summary>
public sealed class PlatformOpsTests
{
    private static readonly FakeLoginLink.Person Priya = new("sub-priya", "priya@acme.in", "Priya", "tenant_admin", []);

    private static string Nupkg(string name)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var file = Path.Combine(dir.FullName, "artifacts", "packages", name);
            if (File.Exists(file)) return file;
        }
        throw new FileNotFoundException(name);
    }

    private sealed class FakeNuGet(byte[] package) : HttpMessageHandler
    {
        public List<string> Urls { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.ToString();
            Urls.Add(url);
            if (url.EndsWith("/index.json")) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"versions":["0.0.9","0.1.0","0.2.0-beta"]}""") });
            if (url.EndsWith(".nupkg")) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(package) });
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    [Fact]
    public async Task The_catalog_gates_methods_and_installs_connector_packages_at_runtime()
    {
        var nuget = new FakeNuGet(File.ReadAllBytes(Nupkg("WebSvc.PayLink.Connector.Simulator.0.1.0.nupkg")));
        await using var ll = await FakeLoginLink.StartAsync();
        await using var app = new PayLinkApp(nuget: nuget);
        var admin = new Browser(app);
        await SetupAsync(app, admin, ll);
        var priya = await CreateTenantAsync(app, ll, "acme", "Acme Learning", Priya);
        var tenant = (await app.Get<PlatformRepository>().TenantBySlugAsync("acme"))!;
        var svc = app.Get<PaymentService>();
        var p = (await svc.CreateAsync(tenant, new CreatePaymentRequest(5000, null, null, null, null, null, null, null, null, null, null), false, null, EventSource.System, "api")).Payment!;
        var payer = new Browser(app);
        Assert.Contains("value=\"simulator\"", await payer.TextAsync(await payer.GetAsync($"/pay/{p.Id}")));

        // Not allowed in Test: tenants can't offer it, whatever they turned on.
        var r = await admin.SubmitAsync("/admin/connectors", "Allow", F(("id", "simulator")));
        Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
        Assert.DoesNotContain("value=\"simulator\"", await payer.TextAsync(await payer.GetAsync($"/pay/{p.Id}")));
        await admin.SubmitAsync("/admin/connectors", "Allow", F(("id", "simulator"), ("test", "true")));
        Assert.Contains("value=\"simulator\"", await payer.TextAsync(await payer.GetAsync($"/pay/{p.Id}")));

        // Only allowed package ids; then the latest released version from nuget.org, loaded in its own context.
        r = await admin.FollowAsync(await admin.SubmitAsync("/admin/connectors", "Install", F(("packageId", "Evil.Package"))));
        Assert.Contains("can be installed", await admin.TextAsync(r));
        r = await admin.FollowAsync(await admin.SubmitAsync("/admin/connectors", "Install", F(("packageId", "WebSvc.PayLink.Connector.Simulator"))));
        Assert.Contains("is installed", await admin.TextAsync(r));
        Assert.Contains(nuget.Urls, u => u.EndsWith("/webSvc.paylink.connector.simulator/0.1.0/websvc.paylink.connector.simulator.0.1.0.nupkg", StringComparison.OrdinalIgnoreCase));
        var loaded = app.Get<ConnectorRegistry>().Get("simulator")!;
        Assert.NotSame(AssemblyLoadContext.Default, AssemblyLoadContext.GetLoadContext(loaded.GetType().Assembly));
        var entry = await app.Get<ConnectorCatalog>().GetAsync("simulator");
        Assert.Equal("package", entry.Source);
        Assert.Equal("0.1.0", entry.PackageVersion);

        // The packaged connector takes payments like the built-in one.
        await payer.SubmitAsync($"/pay/{p.Id}", "Choose", F(("connector", "simulator")));
        Assert.Single(await svc.AttemptsAsync(tenant, p.Id));

        // Draining keeps it off checkouts; uninstalling waits for open attempts.
        await admin.SubmitAsync("/admin/connectors", "State", F(("id", "simulator"), ("state", "draining")));
        var q = (await svc.CreateAsync(tenant, new CreatePaymentRequest(5000, null, null, null, null, null, null, null, null, null, null), false, null, EventSource.System, "api")).Payment!;
        Assert.DoesNotContain("value=\"simulator\"", await new Browser(app).TextAsync(await new Browser(app).GetAsync($"/pay/{q.Id}")));
        r = await admin.FollowAsync(await admin.SubmitAsync("/admin/connectors", "State", F(("id", "simulator"), ("state", "uninstalled"))));
        Assert.Contains("still has 1 open payment attempt", await admin.TextAsync(r));
        await svc.CancelAsync(tenant, p.Id, "done", EventSource.System);
        r = await admin.FollowAsync(await admin.SubmitAsync("/admin/connectors", "State", F(("id", "simulator"), ("state", "uninstalled"))));
        Assert.Contains("is uninstalled", await admin.TextAsync(r));

        // A restart loads the installed package again.
        await admin.SubmitAsync("/admin/connectors", "State", F(("id", "simulator"), ("state", "installed")));
        await admin.SubmitAsync("/admin/connectors", "Install", F(("packageId", "WebSvc.PayLink.Connector.Simulator"), ("version", "0.1.0")));
        Assert.Equal("package", (await app.Get<ConnectorCatalog>().GetAsync("simulator")).Source);
    }

    [Fact]
    public async Task Platform_limits_apply_to_every_tenant()
    {
        await using var ll = await FakeLoginLink.StartAsync();
        await using var app = new PayLinkApp();
        var admin = new Browser(app);
        await SetupAsync(app, admin, ll);
        var priya = await CreateTenantAsync(app, ll, "acme", "Acme Learning", Priya);
        await admin.SubmitAsync("/admin/settings", "Limits", F(("maxAmount", "1000"), ("currencies", "INR")));
        var key = await CreateKeyAsync(priya, "acme");
        var api = new Browser(app).Api;
        api.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
        var big = await api.PostAsJsonAsync("/api/v1/payments", new { amount = 100001 });
        Assert.Equal("amount_too_large", (string?)(await big.Content.ReadFromJsonAsync<JsonObject>())!["error"]);
        var usd = await api.PostAsJsonAsync("/api/v1/payments", new { amount = 100, currency = "USD" });
        Assert.Equal("currency_not_allowed", (string?)(await usd.Content.ReadFromJsonAsync<JsonObject>())!["error"]);
        Assert.Equal(HttpStatusCode.Created, (await api.PostAsJsonAsync("/api/v1/payments", new { amount = 100000 })).StatusCode);
    }

    [Fact]
    public async Task A_tenant_console_can_live_on_its_own_subdomain()
    {
        await using var ll = await FakeLoginLink.StartAsync();
        await using var app = new PayLinkApp();
        var admin = new Browser(app);
        await SetupAsync(app, admin, ll);
        await CreateTenantAsync(app, ll, "acme", "Acme Learning", Priya);

        var r = await admin.FollowAsync(await admin.SubmitAsync("/admin/tenants/acme", "Console", F(("mode", "subdomain"))));
        Assert.Contains("Set the tenant domain first", await admin.TextAsync(r));
        await admin.SubmitAsync("/admin/settings", "Domain", F(("tenantDomain", "pay.example.com")));
        r = await admin.FollowAsync(await admin.SubmitAsync("/admin/tenants/acme", "Console", F(("mode", "subdomain"))));
        var html = await admin.TextAsync(r);
        Assert.Contains("console is at http://acme.pay.example.com", html);
        Assert.Contains(ll.AppPatches.Values, p => p["redirectUris"] is JsonArray uris && uris.Any(u => (string?)u == "http://acme.pay.example.com/auth/callback"));

        // On the subdomain, "/" is the console (sign-in first), and sign-in returns to the subdomain.
        var client = app.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions { AllowAutoRedirect = false, BaseAddress = new Uri("http://acme.pay.example.com") });
        var home = await client.GetAsync("/");
        Assert.Equal("/acme/login", home.Headers.Location?.OriginalString);
        var go = await client.GetAsync("/login?go=true");
        Assert.Contains("redirect_uri=http%3A%2F%2Facme.pay.example.com%2Fauth%2Fcallback", go.Headers.Location!.ToString());
        // Other hosts and other tenants aren't affected.
        var other = app.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions { AllowAutoRedirect = false, BaseAddress = new Uri("http://zeta.pay.example.com") });
        Assert.NotEqual("/zeta/login", (await other.GetAsync("/")).Headers.Location?.OriginalString);
    }

    [Fact]
    public async Task A_tenant_database_moves_with_all_its_data()
    {
        await using var ll = await FakeLoginLink.StartAsync();
        await using var app = new PayLinkApp();
        var admin = new Browser(app);
        await SetupAsync(app, admin, ll);
        var priya = await CreateTenantAsync(app, ll, "acme", "Acme Learning", Priya, db: "sqlite");   // moves start from a SQLite file
        var tenant = (await app.Get<PlatformRepository>().TenantBySlugAsync("acme"))!;
        var svc = app.Get<PaymentService>();
        for (var i = 0; i < 3; i++)
            await svc.CreateAsync(tenant, new CreatePaymentRequest(1000 + i, null, $"M-{i}", null, null, null, null, null, null, null, null), false, null, EventSource.System, "api");
        var before = app.Get<TenantDb>().Target(tenant).SqliteFilePath!;

        var r = await admin.FollowAsync(await admin.SubmitAsync("/admin/tenants/acme", "Move", F(("to", "sqlite"))));
        Assert.Contains("rows copied and counted", await admin.TextAsync(r));
        var moved = (await app.Get<PlatformRepository>().TenantBySlugAsync("acme"))!;
        Assert.Equal("active", moved.Status);
        var after = app.Get<TenantDb>().Target(moved).SqliteFilePath!;
        Assert.NotEqual(before, after);
        Assert.True(File.Exists(before));   // the old database is kept
        Assert.Equal(3, (await svc.SearchAsync(moved, false, null, null)).Count);
        var (_, console) = await ConsoleSignInAsync(new Browser(app), ll, "acme", "acme", Priya);
        Assert.Contains("Acme Learning", console);
        await using var c = await app.Get<TenantDb>().OpenAsync(moved);
        Assert.Contains("tenant.database_moved", await c.QueryAsync<string>("SELECT action FROM audit_log;"));
    }

    /// <summary>Runs only when PAYLINK_TEST_PG_ADMIN is set: SQLite's 0/1 flags and ids land in PostgreSQL's booleans and identities.</summary>
    [Fact]
    public async Task A_tenant_moves_from_sqlite_to_postgresql()
    {
        if (!PgTest.On) return;
        await using var ll = await FakeLoginLink.StartAsync();
        await using var app = new PayLinkApp();
        var admin = new Browser(app);
        await SetupAsync(app, admin, ll, database: SqlitePlatform);
        var priya = await CreateTenantAsync(app, ll, "acme", "Acme Learning", Priya, db: "sqlite");
        var tenant = (await app.Get<PlatformRepository>().TenantBySlugAsync("acme"))!;
        await priya.SubmitAsync("/acme/console/risk", "Add", F(("kind", "amount_above"), ("value", "50000"), ("action", "review")));
        await CreateKeyAsync(priya, "acme");
        var svc = app.Get<PaymentService>();
        await svc.CreateAsync(tenant, new CreatePaymentRequest(1000, null, "M-1", null, null, null, null, null, null, null, null), false, null, EventSource.System, "api");

        var r = await admin.FollowAsync(await admin.SubmitAsync("/admin/tenants/acme", "Move", F(("to", "byo"),
            ("Byo.Engine", "postgresql"), ("Byo.UseConnectionString", "true"), ("Byo.ConnectionString", PgTest.NewDatabase()))));
        Assert.Contains("rows copied and counted", await admin.TextAsync(r));
        var moved = (await app.Get<PlatformRepository>().TenantBySlugAsync("acme"))!;
        Assert.Equal(WebSvc.PayLink.Storage.DbEngine.PostgreSql, app.Get<TenantDb>().Target(moved).Engine);
        Assert.Single(await svc.SearchAsync(moved, false, null, null));
        Assert.Contains("amount_above", await priya.TextAsync(await priya.GetAsync("/acme/console/risk")));
        // New rows after the move get fresh ids (the identity moved past the copied ones).
        await CreateKeyAsync(priya, "acme");
        r = await priya.FollowAsync(await priya.SubmitAsync("/acme/console/risk", "Add", F(("kind", "amount_above"), ("value", "90000"), ("action", "block"))));
        Assert.Contains("Rule added", await priya.TextAsync(r));
    }

    [Fact]
    public async Task The_platform_database_moves_too()
    {
        await using var ll = await FakeLoginLink.StartAsync();
        await using var app = new PayLinkApp();
        var admin = new Browser(app);
        await SetupAsync(app, admin, ll);
        await CreateTenantAsync(app, ll, "acme", "Acme Learning", Priya);
        var r = await admin.FollowAsync(await admin.SubmitAsync("/admin/settings", "MovePlatform", F(("to", "sqlite"))));
        Assert.Contains("The platform database is now", await admin.TextAsync(r));
        Assert.Contains("platform_", (await app.Get<PlatformDb>().TargetAsync()).SqliteFilePath);
        Assert.NotNull(await app.Get<PlatformRepository>().TenantBySlugAsync("acme"));
        Assert.Contains("Platform", await admin.TextAsync(await admin.GetAsync("/admin")));
    }

    [Fact]
    public async Task The_admin_dashboard_adds_up_live_payments_across_tenants()
    {
        await using var ll = await FakeLoginLink.StartAsync();
        await using var app = new PayLinkApp();
        var admin = new Browser(app);
        await SetupAsync(app, admin, ll);
        await CreateTenantAsync(app, ll, "acme", "Acme Learning", Priya);
        var tenant = (await app.Get<PlatformRepository>().TenantBySlugAsync("acme"))!;
        await using (var c = await app.Get<TenantDb>().OpenAsync(tenant))
            await c.ExecuteAsync("INSERT INTO payments (id, livemode, status, amount, currency, created_via, trace_id, created_at, updated_at, expires_at, connector) " +
                                 "VALUES ('pay_live1', TRUE, 'succeeded', 250000, 'INR', 'api', 't', @now, @now, @now, 'razorpay');", new { now = PayLink.Infrastructure.Clock.Now() });
        app.Get<PlatformPayments>().Forget();   // the dashboard caches its numbers for a minute
        var html = await admin.TextAsync(await admin.GetAsync("/admin"));
        Assert.Contains("₹2,500.00", html);
        Assert.Contains("razorpay 1", html);
    }
}
