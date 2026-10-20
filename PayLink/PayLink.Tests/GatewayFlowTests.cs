using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Dapper;
using PayLink.Payments;
using PayLink.Platform;
using PayLink.Tenancy;
using PayLink.Tests.Fakes;
using Xunit;
using static PayLink.Tests.Flows;

namespace PayLink.Tests;

/// <summary>Phase 2 end to end: a gateway (Razorpay, against a fake API) from checkout to refund, plus polling and manual refunds.</summary>
public sealed class GatewayFlowTests
{
    private static readonly FakeLoginLink.Person Priya = new("sub-priya", "priya@acme.in", "Priya", "tenant_admin", []);

    private static string Hmac(string secret, string text) =>
        Convert.ToHexString(new HMACSHA256(Encoding.UTF8.GetBytes(secret)).ComputeHash(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    [Fact]
    public async Task Razorpay_from_checkout_to_refund()
    {
        var rzp = new FakeRazorpay();
        await using var ll = await FakeLoginLink.StartAsync();
        await using var app = new PayLinkApp(gateways: rzp);
        await SetupAsync(app, new Browser(app), ll);
        var priya = await CreateTenantAsync(app, ll, "acme", "Acme Learning", Priya);
        var tenant = (await app.Get<PlatformRepository>().TenantBySlugAsync("acme"))!;

        // Wrong-mode keys are refused; test keys pass the check and turn Razorpay on.
        var r = await priya.FollowAsync(await priya.SubmitAsync("/acme/console/methods", null,
            F(("connector", "razorpay"), ("mode", "test"), ("action", "enable"), ("test.key_id", "rzp_live_x"), ("test.key_secret", "sec"))));
        Assert.Contains("rzp_test_", await priya.TextAsync(r));
        r = await priya.FollowAsync(await priya.SubmitAsync("/acme/console/methods", null,
            F(("connector", "razorpay"), ("mode", "test"), ("action", "enable"), ("test.key_id", "rzp_test_x"), ("test.key_secret", "sec"), ("test.webhook_secret", "whs"))));
        var html = await priya.TextAsync(r);
        Assert.Contains("Razorpay is on in test", html);
        Assert.Contains("/hooks/acme/razorpay/test", html);

        var key = await CreateKeyAsync(priya, "acme");
        var api = new Browser(app).Api;
        api.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
        var created = (await (await api.PostAsJsonAsync("/api/v1/payments", new { amount = 20000, reference = "R-1", customer = new { email = "a@b.in" } })).Content.ReadFromJsonAsync<JsonObject>())!;
        var id = (string)created["id"]!;

        // The payer picks Razorpay and is sent to the payment link.
        var payer = new Browser(app);
        Assert.Contains("value=\"razorpay\"", await payer.TextAsync(await payer.GetAsync($"/pay/{id}")));
        r = await payer.SubmitAsync($"/pay/{id}", "Choose", F(("connector", "razorpay")));
        Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
        Assert.Equal("https://rzp.io/i/plink_T1", r.Headers.Location!.ToString());
        var attempt = (await app.Get<PaymentService>().AttemptsAsync(tenant, id)).Single();
        Assert.Equal("plink_T1", attempt.GatewayRef);

        // A forged callback changes nothing; the signed one marks it paid.
        r = await payer.FollowAsync(await payer.GetAsync($"/pay/{id}/return?attempt={attempt.Id}&razorpay_payment_id=pay_X&razorpay_payment_link_id=plink_T1&razorpay_payment_link_reference_id={attempt.Id}&razorpay_payment_link_status=paid&razorpay_signature=bad"));
        Assert.NotEqual(PaymentStatuses.Succeeded, (await app.Get<PaymentService>().GetAsync(tenant, id))!.Status);
        var sig = Hmac("sec", $"plink_T1|{attempt.Id}|paid|pay_T1");
        r = await payer.FollowAsync(await payer.GetAsync($"/pay/{id}/return?attempt={attempt.Id}&razorpay_payment_id=pay_T1&razorpay_payment_link_id=plink_T1&razorpay_payment_link_reference_id={attempt.Id}&razorpay_payment_link_status=paid&razorpay_signature={sig}"));
        Assert.Contains("Paid", await payer.TextAsync(r));
        var paid = (await app.Get<PaymentService>().GetAsync(tenant, id))!;
        Assert.Equal(PaymentStatuses.Succeeded, paid.Status);
        Assert.Equal("razorpay", paid.Connector);
        Assert.Equal("pay_T1", (await app.Get<PaymentService>().AttemptsAsync(tenant, id)).Single().ProviderPaymentId);

        // A partial refund through the API is pending until Razorpay's webhook says it's processed.
        var refundResponse = await api.PostAsJsonAsync($"/api/v1/payments/{id}/refunds", new { amount = 5000, reason = "Late delivery" });
        Assert.Equal(HttpStatusCode.Created, refundResponse.StatusCode);
        var refund = (await refundResponse.Content.ReadFromJsonAsync<JsonObject>())!;
        Assert.Equal("pending", (string?)refund["status"]);
        Assert.Contains($"POST /v1/payments/pay_T1/refund", rzp.Calls);
        var tooMuch = await api.PostAsJsonAsync($"/api/v1/payments/{id}/refunds", new { amount = 16000 });
        Assert.Equal(HttpStatusCode.BadRequest, tooMuch.StatusCode);

        var refundRow = (await app.Get<PaymentService>().RefundsAsync(tenant, id)).Single();
        var hook = new JsonObject
        {
            ["event"] = "refund.processed",
            ["payload"] = new JsonObject { ["refund"] = new JsonObject { ["entity"] = new JsonObject
            {
                ["id"] = refundRow.GatewayRef, ["payment_id"] = "pay_T1", ["receipt"] = refundRow.Id, ["status"] = "processed",
            } } },
        }.ToJsonString();
        var hookRequest = new HttpRequestMessage(HttpMethod.Post, "/hooks/acme/razorpay/test") { Content = new StringContent(hook, Encoding.UTF8, "application/json") };
        hookRequest.Headers.Add("X-Razorpay-Signature", Hmac("whs", hook));
        Assert.Equal(HttpStatusCode.OK, (await new Browser(app).Api.SendAsync(hookRequest)).StatusCode);
        var bad = new HttpRequestMessage(HttpMethod.Post, "/hooks/acme/razorpay/test") { Content = new StringContent(hook, Encoding.UTF8, "application/json") };
        bad.Headers.Add("X-Razorpay-Signature", Hmac("nope", hook));
        Assert.Equal(HttpStatusCode.BadRequest, (await new Browser(app).Api.SendAsync(bad)).StatusCode);

        var after = (await api.GetFromJsonAsync<JsonObject>($"/api/v1/payments/{id}"))!;
        Assert.Equal(5000, (long)after["amount_refunded"]!);
        Assert.Equal("succeeded", (string?)after["refunds"]![0]!["status"]);
        html = await priya.TextAsync(await priya.GetAsync($"/acme/console/payments/{id}"));
        Assert.Contains("Partly refunded", html);
        Assert.Contains("Refunded ₹50.00", html);

        // A full refund of the rest, processed at once.
        rzp.RefundStatus = "processed";
        r = await priya.FollowAsync(await priya.SubmitAsync($"/acme/console/payments/{id}", "Refund", F(("reason", "Cancelled course"))));
        Assert.Contains("Refunded ₹150.00", await priya.TextAsync(r));
        Assert.Equal(20000, (await app.Get<PaymentService>().GetAsync(tenant, id))!.AmountRefunded);

        // Polling: the payer never came back, but Razorpay says the link was paid.
        var second = (string)(await (await api.PostAsJsonAsync("/api/v1/payments", new { amount = 9900 })).Content.ReadFromJsonAsync<JsonObject>())!["id"]!;
        await new Browser(app).SubmitAsync($"/pay/{second}", "Choose", F(("connector", "razorpay")));
        rzp.LinkStatus["plink_T2"] = "paid";
        await using (var c = await app.Get<TenantDb>().OpenAsync(tenant))
            await c.ExecuteAsync("UPDATE attempts SET created_at = '2020-01-01T00:00:00.000Z' WHERE payment_id = @second;", new { second });
        await app.Get<PaymentJobs>().RunOnceAsync();
        var polled = (await app.Get<PaymentService>().GetAsync(tenant, second))!;
        Assert.Equal(PaymentStatuses.Succeeded, polled.Status);
        Assert.Equal("upi", polled.Method);

        // Unverified notifications are kept for inspection but never change anything.
        await using (var c = await app.Get<TenantDb>().OpenAsync(tenant))
            Assert.Equal(1, await c.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM gateway_events WHERE verified = FALSE;"));
    }

    [Fact]
    public async Task UPI_payments_are_refunded_by_hand_and_recorded()
    {
        await using var ll = await FakeLoginLink.StartAsync();
        await using var app = new PayLinkApp();
        await SetupAsync(app, new Browser(app), ll);
        var priya = await CreateTenantAsync(app, ll, "acme", "Acme Learning", Priya);
        var tenant = (await app.Get<PlatformRepository>().TenantBySlugAsync("acme"))!;
        var svc = app.Get<PaymentService>();
        var p = (await svc.CreateAsync(tenant, new CreatePaymentRequest(10000, null, null, null, null, null, null, null, null, null, null), false, null, EventSource.System, "api")).Payment!;
        var payer = new Browser(app);
        await payer.SubmitAsync($"/pay/{p.Id}", "Choose", F(("connector", "simulator")));
        var a = (await svc.AttemptsAsync(tenant, p.Id)).Single();
        await payer.SubmitAsync($"/pay/{p.Id}", "Simulate", F(("attemptId", a.Id), ("outcome", "succeed")));

        // The Simulator can't refund: PayLink says so, and a manual refund needs a note.
        var gateway = await svc.RefundAsync(tenant, p.Id, null, "x", null, EventSource.System);
        Assert.Equal("refunds_not_supported", gateway.Error?.Code);
        var r = await priya.FollowAsync(await priya.SubmitAsync($"/acme/console/payments/{p.Id}", "Refund", F(("manual", "true"))));
        Assert.Contains("Say how and when you refunded it", await priya.TextAsync(r));
        r = await priya.FollowAsync(await priya.SubmitAsync($"/acme/console/payments/{p.Id}", "Refund", F(("manual", "true"), ("amount", "40"), ("reason", "IMPS ref 1234"))));
        Assert.Contains("Refunded ₹40.00", await priya.TextAsync(r));
        Assert.Equal(4000, (await svc.GetAsync(tenant, p.Id))!.AmountRefunded);
        await using var c = await app.Get<TenantDb>().OpenAsync(tenant);
        Assert.Contains("refund.recorded", await c.QueryAsync<string>("SELECT action FROM audit_log;"));
    }
}
