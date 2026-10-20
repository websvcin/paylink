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

/// <summary>Phase 4: payment links at /l/{code}, their console page and API, and what the drop-in relies on.</summary>
public sealed class LinkTests
{
    private static readonly FakeLoginLink.Person Priya = new("sub-priya", "priya@acme.in", "Priya", "tenant_admin", []);

    private static async Task<(PayLinkApp App, FakeLoginLink LL, Browser Owner, TenantRecord Tenant)> StartAsync()
    {
        var ll = await FakeLoginLink.StartAsync();
        var app = new PayLinkApp();
        await SetupAsync(app, new Browser(app), ll);
        var owner = await CreateTenantAsync(app, ll, "acme", "Acme Learning", Priya);
        return (app, ll, owner, (await app.Get<PlatformRepository>().TenantBySlugAsync("acme"))!);
    }

    private static HttpClient Api(PayLinkApp app, string key)
    {
        var c = new Browser(app).Api;
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
        return c;
    }

    private static async Task<(HttpStatusCode Status, JsonObject Json)> Post(HttpClient api, string url, Dictionary<string, object?> body)
    {
        var r = await api.PostAsync(url, JsonContent.Create(body));
        return (r.StatusCode, JsonNode.Parse(await r.Content.ReadAsStringAsync())!.AsObject());
    }

    private static async Task SucceedAsync(PayLinkApp app, TenantRecord t, Browser payer, string paymentId)
    {
        await payer.SubmitAsync($"/pay/{paymentId}", "Choose", F(("connector", "simulator")));
        var a = (await app.Get<PaymentService>().AttemptsAsync(t, paymentId)).Single();
        await payer.SubmitAsync($"/pay/{paymentId}", "Simulate", F(("attemptId", a.Id), ("outcome", "succeed")));
    }

    [Fact]
    public async Task A_one_time_link_takes_one_payment()
    {
        var (app, ll, priya, tenant) = await StartAsync();
        await using var _ = ll;
        await using var __ = app;
        var r = await priya.FollowAsync(await priya.SubmitAsync("/acme/console/links", "Add", F(("title", "Term fees, Class 6"), ("amountMode", "fixed"),
            ("amount", "1,499.00"), ("askEmail", "true"), ("maxPayments", "1"), ("code", "term-fees"))));
        Assert.Contains("Link created: http://localhost/l/term-fees", await priya.TextAsync(r));

        var payer = new Browser(app);
        var html = await payer.TextAsync(await payer.GetAsync("/l/term-fees"));
        Assert.Contains("₹1,499.00", html);
        Assert.Contains("TEST MODE", html);
        Assert.Contains("data-paylink=", await priya.TextAsync(await priya.GetAsync("/acme/console/links")));

        // Email was asked for, so it's required.
        r = await payer.SubmitAsync("/l/term-fees", null, F(("Name", "Ravi")));
        Assert.Contains("Enter your email address.", await payer.TextAsync(r));
        r = await payer.SubmitAsync("/l/term-fees", null, F(("Name", "Ravi"), ("Email", "ravi@example.in")));
        Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
        var paymentId = r.Headers.Location!.ToString()["/pay/".Length..];
        var p = (await app.Get<PaymentService>().GetAsync(tenant, paymentId))!;
        Assert.Equal((149900L, "ravi@example.in", "link", "Term fees, Class 6"), (p.Amount, p.CustomerEmail, p.CreatedVia, p.Description));
        Assert.NotNull(p.LinkId);
        await SucceedAsync(app, tenant, payer, paymentId);

        Assert.Contains("This link has already been paid.", await payer.TextAsync(await new Browser(app).GetAsync("/l/term-fees")));
        html = await priya.TextAsync(await priya.GetAsync("/acme/console/links"));
        Assert.Contains("1 / 1", html);
        Assert.Contains("Closed", html);

        var qr = await priya.GetAsync($"/acme/console/links?handler=Qr&id={p.LinkId}");
        Assert.Equal("image/svg+xml", qr.Content.Headers.ContentType?.MediaType);
        Assert.Equal(HttpStatusCode.NotFound, (await new Browser(app).GetAsync("/l/nope-nope")).StatusCode);
    }

    [Fact]
    public async Task Payers_choose_the_amount_within_the_limits()
    {
        var (app, ll, priya, tenant) = await StartAsync();
        await using var _ = ll;
        await using var __ = app;
        await priya.SubmitAsync("/acme/console/links", "Add", F(("title", "Donate"), ("amountMode", "payer"), ("minAmount", "100"), ("maxAmount", "50,000"), ("code", "donate")));
        var payer = new Browser(app);
        Assert.Contains("From ₹100.00 to ₹50,000.00", await payer.TextAsync(await payer.GetAsync("/l/donate")));
        Assert.Contains("at least ₹100.00", await payer.TextAsync(await payer.SubmitAsync("/l/donate", null, F(("Amount", "50")))));
        Assert.Contains("Enter the amount", await payer.TextAsync(await payer.SubmitAsync("/l/donate", null, F(("Amount", "")))));
        var r = await payer.SubmitAsync("/l/donate", null, F(("Amount", "2,500.50")));
        var p = (await app.Get<PaymentService>().GetAsync(tenant, r.Headers.Location!.ToString()["/pay/".Length..]))!;
        Assert.Equal(250050, p.Amount);

        // Turned off: payers see it's closed.
        var link = (await app.Get<PaymentLinkService>().ListAsync(tenant, false)).Single();
        await priya.SubmitAsync("/acme/console/links", "Active", F(("id", link.Id), ("active", "false")));
        Assert.Contains("no longer taking payments", await payer.TextAsync(await payer.GetAsync("/l/donate")));
    }

    [Fact]
    public async Task Links_over_the_api_and_the_status_the_drop_in_reads()
    {
        var (app, ll, priya, tenant) = await StartAsync();
        await using var _ = ll;
        await using var __ = app;
        var api = Api(app, await CreateKeyAsync(priya, "acme"));
        var (status, json) = await Post(api, "/api/v1/links", new() { ["title"] = "Workshop seat", ["amount"] = 99900, ["code"] = "workshop", ["max_payments"] = 30, ["ask_phone"] = true });
        Assert.Equal(HttpStatusCode.Created, status);
        Assert.Equal(("link", "http://localhost/l/workshop", false, true), ((string)json["object"]!, (string)json["url"]!, (bool)json["livemode"]!, (bool)json["open"]!));
        var id = (string)json["id"]!;

        (status, json) = await Post(api, "/api/v1/links", new() { ["title"] = "Again", ["amount"] = 100, ["code"] = "workshop" });
        Assert.Equal((HttpStatusCode.Conflict, "code_taken"), (status, (string)json["error"]!));
        (status, json) = await Post(api, "/api/v1/links", new() { ["title"] = "Both", ["amount"] = 100, ["min_amount"] = 50 });
        Assert.Equal(HttpStatusCode.BadRequest, status);
        (status, json) = await Post(api, "/api/v1/links", new() { ["title"] = "Bad code", ["code"] = "Has Spaces" });
        Assert.Equal(HttpStatusCode.BadRequest, status);

        var payer = new Browser(app);
        var r = await payer.SubmitAsync("/l/workshop", null, F(("Phone", "+91 98123 45678")));
        var paymentId = r.Headers.Location!.ToString()["/pay/".Length..];
        var checkout = await payer.TextAsync(await payer.GetAsync($"/pay/{paymentId}?embed=1"));
        Assert.Contains("paylink.embed", checkout);

        var st = await payer.Api.GetAsync($"/pay/{paymentId}/status");
        Assert.Equal("*", st.Headers.GetValues("Access-Control-Allow-Origin").Single());
        Assert.Equal("created", (string)JsonNode.Parse(await st.Content.ReadAsStringAsync())!["status"]!);
        await SucceedAsync(app, tenant, payer, paymentId);
        st = await payer.Api.GetAsync($"/pay/{paymentId}/status");
        Assert.Equal("succeeded", (string)JsonNode.Parse(await st.Content.ReadAsStringAsync())!["status"]!);
        Assert.Equal(HttpStatusCode.NotFound, (await payer.Api.GetAsync("/pay/pay_missing/status")).StatusCode);

        json = JsonNode.Parse(await api.GetStringAsync($"/api/v1/links/{id}"))!.AsObject();
        Assert.Equal((1L, 99900L), ((long)json["payments_succeeded"]!, (long)json["amount_collected"]!));
        var byLink = JsonNode.Parse(await api.GetStringAsync($"/api/v1/payments?link_id={id}"))!["data"]!.AsArray();
        Assert.Equal(paymentId, (string)Assert.Single(byLink)!["id"]!);
        Assert.Equal(id, (string)byLink[0]!["link_id"]!);
        Assert.Single(JsonNode.Parse(await api.GetStringAsync("/api/v1/links"))!["data"]!.AsArray());

        (status, json) = await Post(api, $"/api/v1/links/{id}/deactivate", new());
        Assert.Equal((HttpStatusCode.OK, false, false), (status, (bool)json["active"]!, (bool)json["open"]!));

        // A live key doesn't see test links.
        await priya.GetAsync("/acme/console?mode=live");
        var live = Api(app, await CreateKeyAsync(priya, "acme", live: true));
        Assert.Equal(HttpStatusCode.NotFound, (await live.GetAsync($"/api/v1/links/{id}")).StatusCode);

        var js = await payer.Api.GetAsync("/js/paylink.js");
        Assert.Equal(HttpStatusCode.OK, js.StatusCode);
        Assert.Contains("paylink:result", await js.Content.ReadAsStringAsync());
    }
}
