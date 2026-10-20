using System.Net;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using PayLink.Payments;
using PayLink.Platform;
using PayLink.Tests.Fakes;
using Xunit;
using static PayLink.Tests.Flows;

namespace PayLink.Tests;

/// <summary>Phase 4: the read-only reporting API with CrudApi's filter language.</summary>
public sealed class ReportingTests
{
    private static readonly FakeLoginLink.Person Priya = new("sub-priya", "priya@acme.in", "Priya", "tenant_admin", []);

    [Fact]
    public async Task Datasets_are_read_with_filters_scoped_to_the_key()
    {
        await using var ll = await FakeLoginLink.StartAsync();
        await using var app = new PayLinkApp();
        await SetupAsync(app, new Browser(app), ll);
        var priya = await CreateTenantAsync(app, ll, "acme", "Acme Learning", Priya);
        var tenant = (await app.Get<PlatformRepository>().TenantBySlugAsync("acme"))!;
        var svc = app.Get<PaymentService>();
        var amounts = new long[] { 50000, 149900, 99900, 20000 };
        for (var i = 0; i < amounts.Length; i++)
        {
            var p = (await svc.CreateAsync(tenant, new CreatePaymentRequest(amounts[i], null, $"R-{i}", i % 2 == 0 ? "Term Fees" : "Workshop", null, null, null, null, null, null, null),
                false, null, EventSource.System, "api")).Payment!;
            if (i == 3) continue;   // one stays open
            var payer = new Browser(app);
            await payer.SubmitAsync($"/pay/{p.Id}", "Choose", F(("connector", "simulator")));
            var a = (await svc.AttemptsAsync(tenant, p.Id)).Single();
            await payer.SubmitAsync($"/pay/{p.Id}", "Simulate", F(("attemptId", a.Id), ("outcome", i == 2 ? "fail" : "succeed")));
        }
        await priya.SubmitAsync("/acme/console/links", "Add", F(("title", "Fees"), ("amountMode", "fixed"), ("amount", "100"), ("code", "fees")));

        var api = new Browser(app).Api;
        api.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", await CreateKeyAsync(priya, "acme"));
        async Task<(HttpStatusCode Status, JsonObject Json)> Get(string url)
        {
            var r = await api.GetAsync(url);
            return (r.StatusCode, JsonNode.Parse(await r.Content.ReadAsStringAsync())!.AsObject());
        }
        static string Q(string s) => Uri.EscapeDataString(s);

        var (status, json) = await Get("/api/v1/reporting");
        Assert.Contains(json["data"]!.AsArray(), d => (string)d!["name"]! == "payments");

        (status, json) = await Get($"/api/v1/reporting/payments?filter={Q("payments.status eq 'succeeded' and payments.amount ge 60000")}&count=true");
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal((1L, 149900L), ((long)json["count"]!, (long)json["data"]![0]!["amount"]!));
        Assert.False((bool)json["data"]![0]!["livemode"]!);

        (status, json) = await Get($"/api/v1/reporting/payments?filter={Q("contains(payments.description,'fees') and payments.paid_at ne null")}&display=id,amount,reference&orderBy=amount:asc");
        var rows = json["data"]!.AsArray();
        Assert.Equal(["R-0"], rows.Select(r => (string)r!["reference"]!).ToList());
        Assert.Equal(["id", "amount", "reference"], rows[0]!.AsObject().Select(kv => kv.Key).ToList());

        (status, json) = await Get("/api/v1/reporting/payments?groupBy=status&orderBy=status:asc");
        var groups = json["data"]!.AsArray().ToDictionary(g => (string)g!["status"]!, g => ((long)g!["count"]!, (long)g!["amount"]!));
        Assert.Equal((2L, 199900L), groups["succeeded"]);
        Assert.Equal((1L, 99900L), groups["requires_action"]);   // a failed attempt leaves the payment open for another try
        Assert.Equal((1L, 20000L), groups["created"]);

        (status, json) = await Get("/api/v1/reporting/payments?limit=2&orderBy=amount:desc");
        Assert.Equal((2, true), (json["data"]!.AsArray().Count, (bool)json["has_more"]!));
        (status, json) = await Get("/api/v1/reporting/payments?limit=2&offset=2&orderBy=amount:desc");
        Assert.Equal((2, false), (json["data"]!.AsArray().Count, (bool)json["has_more"]!));

        (status, json) = await Get($"/api/v1/reporting/links?filter={Q("links.active eq true")}");
        Assert.True((bool)json["data"]![0]!["active"]!);
        (status, json) = await Get($"/api/v1/reporting/events?filter={Q("events.type eq 'payment.succeeded'")}&count=true");
        Assert.Equal(2L, (long)json["count"]!);

        // Mistakes and attempts to step outside are refused or harmless.
        foreach (var bad in new[] { "status eq 'succeeded'", "payments.secret eq 'x'", "attempts.id eq 'x'", "payments.amount eq 'x'", "payments.status eq", "payments.livemode gt true" })
        {
            (status, json) = await Get($"/api/v1/reporting/payments?filter={Q(bad)}");
            Assert.True(status == HttpStatusCode.BadRequest && (string)json["error"]! == "invalid_query", bad);
        }
        Assert.Contains("qualify every column", (string)(await Get($"/api/v1/reporting/payments?filter={Q("status eq 'x'")}")).Json["error_description"]!);
        (status, json) = await Get($"/api/v1/reporting/payments?filter={Q("payments.status eq 'x'' OR 1=1 --'")}");
        Assert.Equal((HttpStatusCode.OK, 0), (status, json["data"]!.AsArray().Count));
        Assert.Equal(HttpStatusCode.BadRequest, (await Get("/api/v1/reporting/payments?display=id,ip")).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await Get("/api/v1/reporting/payments?orderBy=amount;DROP TABLE payments")).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await Get("/api/v1/reporting/tenant_api_keys")).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await Get("/api/v1/reporting/attempts?display=next_action")).Status);

        // A live key sees live data only.
        await priya.GetAsync("/acme/console?mode=live");
        var live = new Browser(app).Api;
        live.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", await CreateKeyAsync(priya, "acme", live: true));
        var liveRows = JsonNode.Parse(await live.GetStringAsync("/api/v1/reporting/payments?count=true"))!;
        Assert.Equal(0L, (long)liveRows["count"]!);
        Assert.Equal(0L, (long)JsonNode.Parse(await live.GetStringAsync("/api/v1/reporting/attempts?count=true"))!["count"]!);
    }
}
