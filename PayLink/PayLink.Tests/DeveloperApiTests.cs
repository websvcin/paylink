using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using PayLink.Platform;
using PayLink.Tests.Fakes;
using Xunit;
using static PayLink.Tests.Flows;

namespace PayLink.Tests;

public sealed class DeveloperApiTests
{
    private static HttpClient With(HttpClient client, string key)
    {
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
        return client;
    }

    [Fact]
    public async Task Keys_are_checked_for_capabilities_reach_and_limits()
    {
        await using var ll = await FakeLoginLink.StartAsync();
        await using var app = new PayLinkApp();
        var admin = new Browser(app);
        await SetupAsync(app, admin, ll);
        var repo = app.Get<PlatformRepository>();

        // No key, or an unknown key: 401.
        Assert.Equal(HttpStatusCode.Unauthorized, (await admin.Api.GetAsync("/api/admin/tenants")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await With(admin.Api, "pdk_nope").GetAsync("/api/admin/tenants")).StatusCode);

        // A Dashboard-only key can't list tenants: 403 insufficient_scope.
        var (_, dashboardOnly) = await repo.CreateDeveloperKeyAsync("Monitoring", [Capabilities.Dashboard], "all", null, null, "test");
        var dash = With(admin.Api, dashboardOnly);
        Assert.Equal(HttpStatusCode.OK, (await dash.GetAsync("/api/admin/dashboard")).StatusCode);
        var denied = await dash.GetAsync("/api/admin/tenants");
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        Assert.Equal("insufficient_scope", (string?)(await denied.Content.ReadFromJsonAsync<JsonObject>())!["error"]);

        // A signup-automation key: Tenants + LoginLink, only tenants it creates, at most one.
        var (_, signupKey) = await repo.CreateDeveloperKeyAsync("Signup automation", [Capabilities.Tenants, Capabilities.LoginLink], "own", 1, null, "test");
        var api = With(admin.Api, signupKey);
        var cred = ll.AddManagementCredential("delta");
        var created = await api.PostAsJsonAsync("/api/admin/tenants", new
        {
            name = "Delta Foods", slug = "delta",
            loginLink = new { organization = "delta", clientId = cred.ClientId, clientSecret = cred.Secret },
            database = new { mode = "sqlite" },
        });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var body = (await created.Content.ReadFromJsonAsync<JsonObject>())!;
        Assert.Equal("delta", (string?)body["slug"]);

        var cred2 = ll.AddManagementCredential("epsilon");
        var second = await api.PostAsJsonAsync("/api/admin/tenants", new
        {
            name = "Epsilon", slug = "epsilon",
            loginLink = new { organization = "epsilon", clientId = cred2.ClientId, clientSecret = cred2.Secret },
        });
        Assert.Equal((HttpStatusCode)429, second.StatusCode);

        // Bad input is a 400 with a plain description (checked with a key that still has room under its limit).
        var (_, roomy) = await repo.CreateDeveloperKeyAsync("Roomy", [Capabilities.Tenants, Capabilities.LoginLink], "own", null, null, "test");
        var bad = await With(admin.Api, roomy).PostAsJsonAsync("/api/admin/tenants", new { name = "X", slug = "admin", loginLink = new { organization = "x", clientId = "a", clientSecret = "b" } });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        Assert.Contains("reserved", (string?)(await bad.Content.ReadFromJsonAsync<JsonObject>())!["error_description"]);

        // A key that reaches every tenant sees delta; the 'own' key sees only what it created.
        var (_, allKey) = await repo.CreateDeveloperKeyAsync("Ops", [Capabilities.Tenants], "all", null, null, "test");
        var list = (await With(admin.Api, allKey).GetFromJsonAsync<JsonObject>("/api/admin/tenants"))!;
        Assert.Single(list["tenants"]!.AsArray());

        // Suspend via the API.
        var id = (long)body["id"]!;
        var patched = await With(admin.Api, allKey).PatchAsJsonAsync($"/api/admin/tenants/{id}", new { status = "suspended" });
        Assert.Equal(HttpStatusCode.OK, patched.StatusCode);
        Assert.Equal("suspended", (await repo.TenantByIdAsync(id))!.Status);

        // Revoked keys stop working immediately.
        var key = (await repo.DeveloperKeysAsync()).First(k => k.Name == "Ops");
        await repo.RevokeDeveloperKeyAsync(key.Id);
        Assert.Equal(HttpStatusCode.Unauthorized, (await With(admin.Api, allKey).GetAsync("/api/admin/tenants")).StatusCode);
    }

    [Fact]
    public async Task Keys_are_created_from_the_console_and_shown_once()
    {
        await using var ll = await FakeLoginLink.StartAsync();
        await using var app = new PayLinkApp();
        var admin = new Browser(app);
        await SetupAsync(app, admin, ll);

        var r = await admin.FollowAsync(await admin.SubmitAsync("/admin/developer-api", "Create",
            F(("Name", "Nightly"), ("Caps", "Audit"), ("Reach", "all"))));
        var html = await admin.TextAsync(r);
        var key = System.Text.RegularExpressions.Regex.Match(html, "(pdk_[A-Za-z0-9]+)").Groups[1].Value;
        Assert.StartsWith("pdk_", key);

        var audit = await With(admin.Api, key).GetAsync("/api/admin/audit");
        Assert.Equal(HttpStatusCode.OK, audit.StatusCode);

        // The page doesn't show the key again.
        var again = await admin.TextAsync(await admin.GetAsync("/admin/developer-api"));
        Assert.DoesNotContain(key, again);
    }
}
