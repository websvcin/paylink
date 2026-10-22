using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using PayLink.Infrastructure;
using PayLink.Payments;
using PayLink.Platform;
using PayLink.Tests.Fakes;
using Xunit;
using static PayLink.Tests.Flows;

namespace PayLink.Tests;

/// <summary>Round 5: the console at scale (filters, date ranges, paging, history) and the templates and builders that explain it.</summary>
public sealed class ConsoleScaleTests
{
    private static readonly FakeLoginLink.Person Priya = new("sub-priya", "priya@acme.in", "Priya", "tenant_admin", []);
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 0x0D, 1, 2, 3];

    private static async Task<(PayLinkApp App, FakeLoginLink LL, Browser Admin, Browser Owner, TenantRecord Tenant)> StartAsync()
    {
        var ll = await FakeLoginLink.StartAsync();
        var app = new PayLinkApp();
        var admin = new Browser(app);
        await SetupAsync(app, admin, ll);
        var owner = await CreateTenantAsync(app, ll, "acme", "Acme Learning", Priya);
        return (app, ll, admin, owner, (await app.Get<PlatformRepository>().TenantBySlugAsync("acme"))!);
    }

    private static async Task<string> Pay(PayLinkApp app, TenantRecord t, long amount) =>
        (await app.Get<PaymentService>().CreateAsync(t, new CreatePaymentRequest(amount, null, null, null, null, null, null, null, null, null, null), false, null, EventSource.System, "api")).Payment!.Id;

    [Fact]
    public async Task Payments_filter_by_amount_and_dates_and_export_what_is_shown()
    {
        var (app, ll, _, priya, tenant) = await StartAsync();
        await using var _ll = ll;
        await using var _app = app;
        var small = await Pay(app, tenant, 10000);
        var mid = await Pay(app, tenant, 20000);
        var big = await Pay(app, tenant, 30000);

        var html = await priya.TextAsync(await priya.GetAsync("/acme/console/payments?min=150&max=250"));
        Assert.Contains(mid, html);
        Assert.DoesNotContain(small, html);
        Assert.DoesNotContain(big, html);

        var today = DateTime.UtcNow.ToString("yyyy-MM-dd");
        html = await priya.TextAsync(await priya.GetAsync($"/acme/console/payments?from={today}&to={today}"));
        Assert.Contains(small, html);
        Assert.Contains(big, html);
        html = await priya.TextAsync(await priya.GetAsync("/acme/console/payments?from=2020-01-01&to=2020-01-31"));
        Assert.DoesNotContain(small, html);

        var csv = await priya.TextAsync(await priya.GetAsync("/acme/console/payments?handler=Csv&min=250"));
        Assert.Contains(big, csv);
        Assert.DoesNotContain(mid, csv);
    }

    [Fact]
    public async Task Reports_take_a_custom_range_and_links_page()
    {
        var (app, ll, _, priya, tenant) = await StartAsync();
        await using var _ll = ll;
        await using var _app = app;
        var html = await priya.TextAsync(await priya.GetAsync("/acme/console/reports?from=2026-01-01&to=2026-01-31"));
        Assert.Contains("1 Jan 2026 to 31 Jan 2026", html);

        var links = app.Get<PaymentLinkService>();
        for (var i = 1; i <= 30; i++)
            Assert.Null((await links.CreateAsync(tenant, new CreateLinkRequest($"Fee {i:00}", null, 1000, null, null, null, false, false, null, null, null, null), false, Actor.System)).Error);
        var page1 = await priya.TextAsync(await priya.GetAsync("/acme/console/links"));
        var page2 = await priya.TextAsync(await priya.GetAsync("/acme/console/links?p=2"));
        Assert.Contains("Fee 30", page1);
        Assert.DoesNotContain("Fee 01", page1);
        Assert.Contains("Fee 01", page2);
        Assert.Contains("Fee 07", await priya.TextAsync(await priya.GetAsync("/acme/console/links?q=Fee%2007")));
    }

    [Fact]
    public async Task The_statement_template_matches_UPI_payments_and_they_show_as_decided()
    {
        var (app, ll, _, priya, tenant) = await StartAsync();
        await using var _ll = ll;
        await using var _app = app;
        await priya.SubmitAsync("/acme/console/methods", null,
            F(("connector", "upi"), ("mode", "test"), ("action", "enable"), ("test.vpa", "acme@okhdfcbank"), ("test.payee_name", "Acme Learning"), ("test.business_vpa_confirmed", "true")));
        var svc = app.Get<PaymentService>();
        var ids = new List<string>();
        foreach (var (amount, utr) in new[] { (149900L, "412345678901"), (50000L, "423456789012") })
        {
            var id = await Pay(app, tenant, amount);
            var payer = new Browser(app);
            await payer.SubmitAsync($"/pay/{id}", "Choose", F(("connector", "upi")));
            var a = (await svc.AttemptsAsync(tenant, id)).Single();
            await payer.SubmitWithFileAsync($"/pay/{id}", "Utr", F(("attemptId", a.Id), ("utr", utr)), "screenshot", "paid.png", Png, "image/png");
            ids.Add(id);
        }

        var template = await priya.GetAsync("/acme/console/upi?handler=StatementTemplate");
        Assert.Equal("text/csv", template.Content.Headers.ContentType?.MediaType);
        var bytes = await template.Content.ReadAsByteArrayAsync();
        var r = await priya.FollowAsync(await priya.SubmitWithFileAsync("/acme/console/upi", "Statement", F(), "statement", "bank.csv", bytes, "text/csv"));
        Assert.Contains("2 credits read: 2 UPI payments approved", await priya.TextAsync(r));

        var decided = await priya.TextAsync(await priya.GetAsync("/acme/console/upi?view=decided&decision=approved"));
        Assert.Contains(ids[0], decided);
        Assert.Contains(ids[1], decided);
        Assert.DoesNotContain(ids[0], await priya.TextAsync(await priya.GetAsync("/acme/console/upi?view=decided&decision=rejected")));
    }

    [Fact]
    public async Task The_settlement_template_maps_itself()
    {
        var (app, ll, _, priya, _) = await StartAsync();
        await using var _ll = ll;
        await using var _app = app;
        var bytes = await (await priya.GetAsync("/acme/console/reconciliation?handler=Template")).Content.ReadAsByteArrayAsync();
        var r = await priya.SubmitWithFileAsync("/acme/console/reconciliation", "Upload", F(("connector", "razorpay")), "report", "settlement.csv", bytes, "text/csv");
        var html = await priya.TextAsync(await priya.GetAsync(r.Headers.Location!.ToString()));
        Assert.Contains("<option value=\"0\" selected>Payment ID</option>", html);
        Assert.Contains("<option value=\"2\" selected>Fee</option>", html);
    }

    [Fact]
    public async Task Risk_presets_overview_sign_in_card_and_the_CrudApi_address()
    {
        var (app, ll, admin, priya, _) = await StartAsync();
        await using var _ll = ll;
        await using var _app = app;

        var html = await priya.TextAsync(await priya.GetAsync("/acme/console/risk"));
        Assert.Contains("Block throwaway emails", html);
        await priya.SubmitAsync("/acme/console/risk", "Add", F(("kind", "tor"), ("value", "yes"), ("action", "block"), ("note", "Block Tor")));
        Assert.Contains("Payer uses Tor", await priya.TextAsync(await priya.GetAsync("/acme/console/risk")));

        html = await priya.TextAsync(await priya.GetAsync("/acme/console"));
        Assert.Contains("Sign-in (LoginLink)", html);
        Assert.Contains(ll.BaseUrl, html);

        Assert.Contains(PlatformSettings.DefaultCrudApiUrl, await priya.TextAsync(await priya.GetAsync("/acme/console/data")));
        var r = await admin.FollowAsync(await admin.SubmitAsync("/admin/settings", "CrudApi", F(("url", "https://crud.example.in/"))));
        Assert.Contains("https://crud.example.in", await admin.TextAsync(r));
        Assert.Contains("https://crud.example.in", await priya.TextAsync(await priya.GetAsync("/acme/console/data")));
    }

    [Fact]
    public async Task Scripts_upload_statements_and_read_decisions_and_the_reference_is_published()
    {
        var (app, ll, _, priya, tenant) = await StartAsync();
        await using var _ll = ll;
        await using var _app = app;
        await priya.SubmitAsync("/acme/console/methods", null,
            F(("connector", "upi"), ("mode", "test"), ("action", "enable"), ("test.vpa", "acme@okhdfcbank"), ("test.payee_name", "Acme Learning"), ("test.business_vpa_confirmed", "true")));
        var svc = app.Get<PaymentService>();
        var id = await Pay(app, tenant, 149900);
        var payer = new Browser(app);
        await payer.SubmitAsync($"/pay/{id}", "Choose", F(("connector", "upi")));
        var a = (await svc.AttemptsAsync(tenant, id)).Single();
        await payer.SubmitWithFileAsync($"/pay/{id}", "Utr", F(("attemptId", a.Id), ("utr", "412345678901")), "screenshot", "paid.png", Png, "image/png");

        var tdk = await CreateKeyAsync(priya, "acme", "developer", false, TenantCapabilities.UpiVerify);
        var api = new Browser(app).Api;
        api.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tdk);
        var csv = "Date,Narration,Ref No,Withdrawal,Deposit,Balance\n21/10/2026,UPI/412345678901/RAVI/ravi@okhdfcbank,412345678901,,1499.00,25499.00\n";
        var r = await api.PostAsync("/api/tenant/upi/statements", new StringContent(csv, Encoding.UTF8, "text/csv"));
        var json = JsonNode.Parse(await r.Content.ReadAsStringAsync())!;
        Assert.True(r.IsSuccessStatusCode, json.ToJsonString());
        Assert.Equal(1, (int)json["approved"]!);

        var form = new MultipartFormDataContent { { new ByteArrayContent(Encoding.UTF8.GetBytes("nothing,useful\n")), "statement", "s.csv" } };
        Assert.Equal(422, (int)(await api.PostAsync("/api/tenant/upi/statements", form)).StatusCode);

        json = JsonNode.Parse(await api.GetStringAsync("/api/tenant/upi/decided?decision=approved"))!;
        Assert.Equal(id, (string)json["data"]![0]!["payment_id"]!);
        Assert.Equal(400, (int)(await api.GetAsync("/api/tenant/upi/decided?decision=maybe")).StatusCode);

        var open = new Browser(app).Api;
        var spec = JsonNode.Parse(await open.GetStringAsync("/docs/api/tenant/openapi.json"))!;
        Assert.NotNull(spec["paths"]!["/api/tenant/upi/statements"]);
        Assert.Null(JsonNode.Parse(await open.GetStringAsync("/docs/api/payments/openapi.json"))!["paths"]!["/api/tenant/upi/pending"]);
        Assert.Contains("sk_live_", await open.GetStringAsync("/docs"));
        Assert.Contains("/api/tenant/upi/statements", await open.GetStringAsync("/docs/automation"));
    }
}
