using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using PayLink.Payments;
using PayLink.Platform;
using PayLink.Tests.Fakes;
using Xunit;
using static PayLink.Tests.Flows;

namespace PayLink.Tests;

/// <summary>Round 6: the tenant API covers the console, and a platform key can act for any tenant in its reach.</summary>
public sealed class AutomationApiTests
{
    private static readonly FakeLoginLink.Person Priya = new("sub-priya", "priya@acme.in", "Priya", "tenant_admin", []);

    private static HttpClient Client(PayLinkApp app, string key, string? tenant = null, string? mode = null)
    {
        var c = new Browser(app).Api;
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
        if (tenant is not null) c.DefaultRequestHeaders.Add("PayLink-Tenant", tenant);
        if (mode is not null) c.DefaultRequestHeaders.Add("PayLink-Mode", mode);
        return c;
    }

    private static async Task<JsonNode> Json(HttpResponseMessage r, HttpStatusCode expected)
    {
        var text = await r.Content.ReadAsStringAsync();
        Assert.True(r.StatusCode == expected, $"{(int)r.StatusCode} {text}");
        return string.IsNullOrEmpty(text) ? new JsonObject() : JsonNode.Parse(text)!;
    }

    [Fact]
    public async Task A_developer_key_with_every_capability_can_run_the_whole_console()
    {
        await using var ll = await FakeLoginLink.StartAsync();
        await using var app = new PayLinkApp();
        await SetupAsync(app, new Browser(app), ll);
        var priya = await CreateTenantAsync(app, ll, "acme", "Acme Learning", Priya);
        var tdk = await CreateKeyAsync(priya, "acme", "developer", false, TenantCapabilities.All.Select(c => c.Key).ToArray());
        var api = Client(app, tdk);

        // Methods and routing.
        await Json(await api.PutAsJsonAsync("/api/tenant/methods/upi/test", new { enabled = true, settings = new Dictionary<string, string>
            { ["vpa"] = "acme@okhdfcbank", ["payee_name"] = "Acme Learning", ["business_vpa_confirmed"] = "true" } }), HttpStatusCode.OK);
        var routing = await Json(await api.PutAsJsonAsync("/api/tenant/routing", new { methods = new[] { new { connector = "upi", order = 1, min_amount = 100, max_amount = 5000000 } }, failover = false }), HttpStatusCode.OK);
        Assert.False((bool)routing["failover"]!);
        Assert.Equal(5000000, (long)routing["data"]![0]!["max_amount"]!);

        // Payments: create, list with filters, export, overview.
        var p = await Json(await api.PostAsJsonAsync("/api/tenant/payments", new { amount = 49900, reference = "INV-1" }), HttpStatusCode.Created);
        var id = (string)p["id"]!;
        var list = await Json(await api.GetAsync("/api/tenant/payments?min_amount=40000&reference=INV-1"), HttpStatusCode.OK);
        Assert.Equal(1, (int)list["total"]!);
        Assert.Contains(id, await (await api.GetAsync("/api/tenant/payments/export")).Content.ReadAsStringAsync());
        var overview = await Json(await api.GetAsync("/api/tenant/overview"), HttpStatusCode.OK);
        Assert.Equal("acme", (string)overview["tenant"]!["slug"]!);

        // Links.
        var link = await Json(await api.PostAsJsonAsync("/api/tenant/links", new { title = "Term fees", amount = 150000 }), HttpStatusCode.Created);
        var linkId = (string)link["id"]!;
        Assert.False((bool)(await Json(await api.PatchAsJsonAsync($"/api/tenant/links/{linkId}", new { active = false }), HttpStatusCode.OK))["active"]!);
        Assert.Equal("image/svg+xml", (await api.GetAsync($"/api/tenant/links/{linkId}/qr")).Content.Headers.ContentType?.MediaType);

        // Reports, reconciliation, disputes.
        Assert.NotNull((await Json(await api.GetAsync("/api/tenant/reports?range=7"), HttpStatusCode.OK))["report"]);
        var csv = "Payment ID,Amount,Fee,Tax,Settled On,Settlement ID\npay_UNKNOWN,100.00,2.00,0.36,2026-10-20,setl_1\n";
        var batch = await Json(await api.PostAsync("/api/tenant/settlements?connector=razorpay", new StringContent(csv, System.Text.Encoding.UTF8, "text/csv")), HttpStatusCode.Created);
        Assert.Equal(1, (int)batch["batch"]!["unmatched"]!);
        await Json(await api.GetAsync("/api/tenant/settlements"), HttpStatusCode.OK);
        await Json(await api.GetAsync("/api/tenant/disputes"), HttpStatusCode.OK);

        // Alerts, risk, webhooks.
        await Json(await api.PostAsJsonAsync("/api/tenant/alerts", new { kind = AlertService.Kinds[0].Kind, threshold = "80", window_minutes = 60, channel = "webhook" }), HttpStatusCode.Created);
        Assert.Single((await Json(await api.GetAsync("/api/tenant/alerts"), HttpStatusCode.OK))["data"]!.AsArray());
        await Json(await api.PostAsJsonAsync("/api/tenant/risk-rules", new { kind = "tor", value = "yes", action = "block" }), HttpStatusCode.Created);

        // Team, domain, audit.
        var team = await Json(await api.GetAsync("/api/tenant/team"), HttpStatusCode.OK);
        var me = team["members"]!.AsArray().First(m => (string)m!["email"]! == "priya@acme.in")!;
        var r = await api.PostAsJsonAsync($"/api/tenant/team/members/{(long)me["id"]!}", new { decision = "remove" });
        Assert.Equal(HttpStatusCode.Conflict, r.StatusCode);   // the last owner stays
        await Json(await api.PutAsJsonAsync("/api/tenant/team/admission", new { mode = "approve_only" }), HttpStatusCode.OK);
        var dom = await Json(await api.PutAsJsonAsync("/api/tenant/domain", new { domain = "pay.acme.in" }), HttpStatusCode.Created);
        Assert.StartsWith("_paylink", (string)dom["domain"]!["txt_record"]!["name"]!);
        var audit = await Json(await api.GetAsync("/api/tenant/audit?verify=true"), HttpStatusCode.OK);
        Assert.Equal("intact", (string)audit["chain"]!);

        // Keys: a narrower key can't hand out more than it has.
        var created = await Json(await api.PostAsJsonAsync("/api/tenant/keys", new { name = "UPI bot", kind = "developer", capabilities = new[] { "upi.verify", "keys" } }), HttpStatusCode.Created);
        var narrow = Client(app, (string)created["secret"]!);
        Assert.Equal(HttpStatusCode.Forbidden, (await narrow.GetAsync("/api/tenant/team")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await narrow.PostAsJsonAsync("/api/tenant/keys", new { name = "x", kind = "developer", capabilities = new[] { "team" } })).StatusCode);
        var sk = await Json(await narrow.PostAsJsonAsync("/api/tenant/keys", new { name = "Shop", kind = "secret", livemode = false }), HttpStatusCode.Created);
        Assert.StartsWith("sk_test_acme_", (string)sk["secret"]!);

        // The OpenAPI document lists the new routes.
        var spec = await new Browser(app).Api.GetStringAsync("/docs/api/tenant/openapi.json");
        foreach (var route in new[] { "/api/tenant/links", "/api/tenant/routing", "/api/tenant/team/members/{id}", "/api/tenant/domain", "/api/tenant/keys", "/api/tenant/settlements" })
            Assert.Contains(route, spec);
    }

    [Fact]
    public async Task A_platform_key_acts_for_tenants_in_its_reach()
    {
        await using var ll = await FakeLoginLink.StartAsync();
        await using var app = new PayLinkApp();
        var admin = new Browser(app);
        await SetupAsync(app, admin, ll);
        await CreateTenantAsync(app, ll, "acme", "Acme Learning", Priya);
        var tenant = (await app.Get<PlatformRepository>().TenantBySlugAsync("acme"))!;

        async Task<string> PlatformKey(params string[] caps)
        {
            var fields = F(("Name", "Product " + string.Join("+", caps)), ("Reach", "all"));
            for (var i = 0; i < caps.Length; i++) fields[$"Caps[{i}]"] = caps[i];
            var html = await admin.TextAsync(await admin.FollowAsync(await admin.SubmitAsync("/admin/developer-api", "Create", fields)));
            return System.Text.RegularExpressions.Regex.Match(html, "(pdk_[A-Za-z0-9]+)").Groups[1].Value;
        }
        var product = await PlatformKey(Capabilities.Tenants, Capabilities.TenantOperations);
        var auditOnly = await PlatformKey(Capabilities.Audit);

        // Tenant API for acme, with every capability.
        var acme = Client(app, product, "acme");
        await Json(await acme.PutAsJsonAsync("/api/tenant/methods/upi/test", new { enabled = true, settings = new Dictionary<string, string>
            { ["vpa"] = "acme@okhdfcbank", ["payee_name"] = "Acme Learning", ["business_vpa_confirmed"] = "true" } }), HttpStatusCode.OK);
        Assert.Equal(HttpStatusCode.OK, (await acme.GetAsync("/api/tenant/team")).StatusCode);

        // Payments API for acme, in test mode.
        var pay = await Json(await Client(app, product, "acme", "test").PostAsJsonAsync("/api/v1/payments", new { amount = 49900, currency = "INR" }), HttpStatusCode.Created);
        Assert.False((bool)pay["livemode"]!);

        // A key without TenantOperations, an unknown tenant, or no header: refused.
        Assert.Equal(HttpStatusCode.Forbidden, (await Client(app, auditOnly, "acme").GetAsync("/api/tenant/team")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Client(app, product, "nobody").GetAsync("/api/tenant/team")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Client(app, product).GetAsync("/api/tenant/team")).StatusCode);

        // Minting keys for a tenant through the platform API.
        var key = await Json(await Client(app, product).PostAsJsonAsync($"/api/admin/tenants/{tenant.Id}/keys", new { name = "Product server", kind = "secret", livemode = false }), HttpStatusCode.Created);
        var sk = (string)key["secret"]!;
        await Json(await Client(app, sk).PostAsJsonAsync("/api/v1/payments", new { amount = 10000, currency = "INR" }), HttpStatusCode.Created);
        Assert.Contains("Product server", (await Json(await Client(app, product).GetAsync($"/api/admin/tenants/{tenant.Id}/keys"), HttpStatusCode.OK)).ToJsonString());
        await Json(await Client(app, product).DeleteAsync($"/api/admin/tenants/{tenant.Id}/keys/{(long)key["id"]!}"), HttpStatusCode.NoContent);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Client(app, sk).GetAsync("/api/v1/payments")).StatusCode);

        // The tenant's audit log names the platform key.
        Assert.Contains("platform key Product", await AuditText(app, tenant));
    }

    private static async Task<string> AuditText(PayLinkApp app, TenantRecord tenant)
    {
        await using var c = await app.Get<PayLink.Tenancy.TenantDb>().OpenAsync(tenant);
        return string.Join("\n", (await PayLink.Infrastructure.AuditLog.RecentAsync(c, 200)).Select(e => e.ActorName));
    }
}
