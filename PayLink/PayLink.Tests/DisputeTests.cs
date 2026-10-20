using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using PayLink.Payments;
using PayLink.Platform;
using PayLink.Tests.Fakes;
using WebSvc.PayLink.Connector.Stripe;
using Xunit;
using static PayLink.Tests.Flows;

namespace PayLink.Tests;

/// <summary>Phase 4: Stripe end to end (against a fake API) and disputes from signed webhooks.</summary>
public sealed class DisputeTests
{
    private static readonly FakeLoginLink.Person Priya = new("sub-priya", "priya@acme.in", "Priya", "tenant_admin", []);

    private static async Task<HttpResponseMessage> StripeHook(Browser b, string body)
    {
        var t = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
        var request = new HttpRequestMessage(HttpMethod.Post, "/hooks/acme/stripe/test") { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        request.Headers.Add("Stripe-Signature", $"t={t},v1={StripeConnector.Sign("whsec_abc", t, body)}");
        return await b.Api.SendAsync(request);
    }

    [Fact]
    public async Task Stripe_payments_and_their_disputes()
    {
        var stripe = new FakeStripe();
        await using var ll = await FakeLoginLink.StartAsync();
        await using var app = new PayLinkApp(gateways: stripe);
        await using var hooks = await WebhookReceiver.StartAsync();
        await SetupAsync(app, new Browser(app), ll);
        var priya = await CreateTenantAsync(app, ll, "acme", "Acme Learning", Priya);
        var tenant = (await app.Get<PlatformRepository>().TenantBySlugAsync("acme"))!;
        await app.Get<WebhookService>().AddEndpointAsync(tenant, hooks.Url, false, null, null, PayLink.Infrastructure.Actor.System);

        var r = await priya.FollowAsync(await priya.SubmitAsync("/acme/console/methods", null,
            F(("connector", "stripe"), ("mode", "test"), ("action", "enable"), ("test.secret_key", "sk_live_x"))));
        Assert.Contains("sk_test_", await priya.TextAsync(r));
        r = await priya.FollowAsync(await priya.SubmitAsync("/acme/console/methods", null,
            F(("connector", "stripe"), ("mode", "test"), ("action", "enable"), ("test.secret_key", "sk_test_x"), ("test.webhook_secret", "whsec_abc"))));
        Assert.Contains("Stripe is on in test", await priya.TextAsync(r));

        var api = new Browser(app).Api;
        api.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", await CreateKeyAsync(priya, "acme"));
        var created = (await (await api.PostAsJsonAsync("/api/v1/payments", new { amount = 2599, currency = "USD", description = "Workshop" })).Content.ReadFromJsonAsync<JsonObject>())!;
        var id = (string)created["id"]!;

        var payer = new Browser(app);
        r = await payer.SubmitAsync($"/pay/{id}", "Choose", F(("connector", "stripe")));
        Assert.Equal("https://checkout.stripe.com/c/pay/cs_test_1", r.Headers.Location!.ToString());
        var attempt = (await app.Get<PaymentService>().AttemptsAsync(tenant, id)).Single();

        // Stripe's return isn't signed: PayLink asks Stripe, which says paid.
        stripe.SessionStatus["cs_test_1"] = "paid";
        r = await payer.FollowAsync(await payer.GetAsync($"/pay/{id}/return?attempt={attempt.Id}"));
        Assert.Contains("Paid", await payer.TextAsync(r));
        var paid = (await app.Get<PaymentService>().GetAsync(tenant, id))!;
        Assert.Equal((PaymentStatuses.Succeeded, "stripe"), (paid.Status, paid.Connector));
        Assert.Equal("pi_cs_test_1", (await app.Get<PaymentService>().AttemptsAsync(tenant, id)).Single().ProviderPaymentId);

        // A dispute: unsigned is refused, signed is recorded and sent on.
        var due = DateTimeOffset.UtcNow.AddDays(7).ToUnixTimeSeconds();
        var opened = """{"type":"charge.dispute.created","data":{"object":{"id":"dp_1","amount":2599,"currency":"usd","reason":"fraudulent","status":"needs_response","payment_intent":"pi_cs_test_1","evidence_details":{"due_by":""" + due + "}}}}";
        var bad = new HttpRequestMessage(HttpMethod.Post, "/hooks/acme/stripe/test") { Content = new StringContent(opened) };
        bad.Headers.Add("Stripe-Signature", "t=1,v1=00");
        Assert.Equal(HttpStatusCode.BadRequest, (await payer.Api.SendAsync(bad)).StatusCode);
        var ok = await StripeHook(payer, opened);
        Assert.Contains("\"matched\":true", await ok.Content.ReadAsStringAsync());
        await StripeHook(payer, opened);   // a repeat changes nothing

        var dispute = Assert.Single(await app.Get<PaymentService>().DisputesAsync(tenant, false));
        Assert.Equal((id, DisputeStatuses.NeedsResponse, 2599L, "USD", "fraudulent"), (dispute.PaymentId, dispute.Status, dispute.Amount, dispute.Currency, dispute.Reason));
        Assert.NotNull(dispute.RespondBy);
        var html = await priya.TextAsync(await priya.GetAsync("/acme/console/disputes"));
        Assert.Contains("Needs your response", html);
        Assert.Contains("Dispute: Needs your response", await priya.TextAsync(await priya.GetAsync($"/acme/console/payments/{id}")));
        r = await priya.FollowAsync(await priya.SubmitAsync("/acme/console/disputes", "Note", F(("id", dispute.Id), ("note", "Sent the attendance sheet"))));
        Assert.Contains("Sent the attendance sheet", await priya.TextAsync(r));

        await StripeHook(payer, opened.Replace("charge.dispute.created", "charge.dispute.closed").Replace("needs_response", "won"));
        Assert.Equal(DisputeStatuses.Won, (await app.Get<PaymentService>().DisputesAsync(tenant, false)).Single().Status);

        await app.Get<PaymentJobs>().RunOnceAsync();
        var types = hooks.Received.Select(d => d.EventType).ToList();
        Assert.Equal(1, types.Count(t => t == "dispute.created"));
        Assert.Equal(1, types.Count(t => t == "dispute.closed"));
        var list = JsonNode.Parse(await api.GetStringAsync($"/api/v1/disputes?payment_id={id}"))!["data"]!.AsArray();
        Assert.Equal(("won", "Sent the attendance sheet"), ((string)list.Single()!["status"]!, (string)list.Single()!["note"]!));
        var journey = (await app.Get<PaymentService>().DetailAsync(tenant, id))!.Value.Events.Select(e => e.Type).ToList();
        Assert.Contains("dispute.opened", journey);
        Assert.Contains("dispute.won", journey);
    }
}
