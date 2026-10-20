using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
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

/// <summary>Phase 1 end to end on SQLite: Payments API, hosted checkout, Simulator, PayLink UPI, webhooks, risk rules, keys.</summary>
public sealed class PaymentFlowTests
{
    private static readonly FakeLoginLink.Person Priya = new("sub-priya", "priya@acme.in", "Priya", "tenant_admin", []);

    private static HttpClient Api(PayLinkApp app, string key)
    {
        var c = new Browser(app).Api;
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
        return c;
    }

    private static async Task<(HttpResponseMessage Response, JsonObject Json)> PostAsync(HttpClient api, string url, object body, string? idempotencyKey = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(body) };
        if (idempotencyKey is not null) request.Headers.Add("Idempotency-Key", idempotencyKey);
        var r = await api.SendAsync(request);
        return (r, (JsonNode.Parse(await r.Content.ReadAsStringAsync()) as JsonObject)!);
    }

    private static async Task<JsonObject> GetJsonAsync(HttpClient api, string url) => (await api.GetFromJsonAsync<JsonObject>(url))!;

    private static async Task<(PayLinkApp App, FakeLoginLink LL, Browser Owner)> StartAsync()
    {
        var ll = await FakeLoginLink.StartAsync();
        var app = new PayLinkApp();
        await SetupAsync(app, new Browser(app), ll);
        var owner = await CreateTenantAsync(app, ll, "acme", "Acme Learning", Priya);
        return (app, ll, owner);
    }

    [Fact]
    public async Task A_merchant_integrates_once_and_takes_a_test_payment_with_the_simulator()
    {
        var (app, ll, priya) = await StartAsync();
        await using var _ = ll;
        await using var __ = app;
        await using var hooks = await WebhookReceiver.StartAsync();

        // Developers page: a test secret key and a webhook endpoint (its secret is shown once).
        var key = await CreateKeyAsync(priya, "acme");
        Assert.StartsWith("sk_test_acme_", key);
        var r = await priya.FollowAsync(await priya.SubmitAsync("/acme/console/developers", "Endpoint", F(("url", hooks.Url), ("description", "orders"))));
        var whsec = System.Text.RegularExpressions.Regex.Match(await priya.TextAsync(r), "whsec_[A-Za-z0-9]{32}").Value;
        Assert.NotEmpty(whsec);

        // POST /api/v1/payments, with an Idempotency-Key.
        var api = Api(app, key);
        var body = new
        {
            amount = 149900, currency = "INR", reference = "ACM-20931", description = "Full-stack course",
            customer = new { name = "Rahul Verma", email = "rahul@example.com", phone = "+919812345678" },
            return_url = "https://acme.in/orders/ACM-20931", metadata = new Dictionary<string, string> { ["plan"] = "monthly" },
        };
        var (created, payment) = await PostAsync(api, "/api/v1/payments", body, "ord-ACM-20931");
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var id = (string)payment["id"]!;
        Assert.StartsWith("pay_", id);
        Assert.Equal("created", (string?)payment["status"]);
        Assert.False((bool)payment["livemode"]!);
        Assert.Equal($"http://localhost/pay/{id}", (string?)payment["checkout_url"]);
        Assert.Equal("monthly", (string?)payment["metadata"]!["plan"]);

        // The same key and body returns the same payment; a different body with the same key is refused.
        var (replayed, again) = await PostAsync(api, "/api/v1/payments", body, "ord-ACM-20931");
        Assert.Equal(HttpStatusCode.OK, replayed.StatusCode);
        Assert.Equal("true", replayed.Headers.GetValues("Idempotent-Replayed").Single());
        Assert.Equal(id, (string?)again["id"]);
        var (conflict, error) = await PostAsync(api, "/api/v1/payments", new { amount = 100 }, "ord-ACM-20931");
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        Assert.Equal("idempotency_conflict", (string?)error["error"]);
        // Without a key, the same reference returns the open payment instead of starting a second one.
        var (sameRef, sameRefJson) = await PostAsync(api, "/api/v1/payments", body);
        Assert.Equal(HttpStatusCode.OK, sameRef.StatusCode);
        Assert.Equal(id, (string?)sameRefJson["id"]);
        var (bad, badJson) = await PostAsync(api, "/api/v1/payments", new { amount = 0 });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        Assert.Equal("invalid_request", (string?)badJson["error"]);

        // The payer opens the checkout: test mode, and the Simulator is offered.
        var payer = new Browser(app);
        var html = await payer.TextAsync(await payer.GetAsync($"/pay/{id}"));
        Assert.Contains("TEST MODE", html);
        Assert.Contains("₹1,499.00", html);
        Assert.Contains("value=\"simulator\"", html);
        Assert.DoesNotContain("value=\"upi\"", html);

        // Fail first, then pay: still one payment, with two attempts.
        r = await payer.FollowAsync(await payer.SubmitAsync($"/pay/{id}", "Choose", F(("connector", "simulator"))));
        html = await payer.TextAsync(r);
        Assert.Contains("value=\"succeed\"", html);
        var attempt1 = (await app.Get<PaymentService>().AttemptsAsync(Tenant(app), id)).Single();
        r = await payer.FollowAsync(await payer.SubmitAsync($"/pay/{id}", "Simulate", F(("attemptId", attempt1.Id), ("outcome", "fail"))));
        Assert.Contains("Choose how to pay", await payer.TextAsync(r));
        r = await payer.FollowAsync(await payer.SubmitAsync($"/pay/{id}", "Choose", F(("connector", "simulator"))));
        var attempt2 = (await app.Get<PaymentService>().AttemptsAsync(Tenant(app), id)).Last();
        r = await payer.FollowAsync(await payer.SubmitAsync($"/pay/{id}", "Simulate", F(("attemptId", attempt2.Id), ("outcome", "succeed"))));
        html = await payer.TextAsync(r);
        Assert.Contains("Paid", html);
        Assert.Contains("https://acme.in/orders/ACM-20931?payment_id=" + id + "&amp;status=succeeded", html);

        // The API says succeeded, paid with the Simulator.
        var got = await GetJsonAsync(api, $"/api/v1/payments/{id}");
        Assert.Equal("succeeded", (string?)got["status"]);
        Assert.Equal("simulator", (string?)got["paid_with"]!["connector"]);
        Assert.Equal(2, got["attempts"]!.AsArray().Count);

        // The background job delivers one signed payment.succeeded webhook.
        await app.Get<PaymentJobs>().RunOnceAsync();
        var delivery = Assert.Single(hooks.Received, d => d.EventType == "payment.succeeded");
        Assert.Equal(id, (string?)delivery.Json["data"]!["id"]);
        var t = long.Parse(delivery.Signature.Split(',')[0][2..]);
        Assert.Equal(WebhookService.Sign(whsec, t, delivery.Body), delivery.Signature);

        // A late "fail" can't undo the success.
        var late = await app.Get<PaymentService>().SimulateAsync(Tenant(app), id, attempt2.Id, "fail", EventSource.System);
        Assert.Equal("final", late.Error?.Code);
        Assert.Equal("succeeded", (string?)(await GetJsonAsync(api, $"/api/v1/payments/{id}"))["status"]);

        // The console shows the journey, the webhook and the API calls.
        html = await priya.TextAsync(await priya.GetAsync($"/acme/console/payments/{id}"));
        Assert.Contains("Created through the API", html);
        Assert.Contains("Payer chose simulator", html);
        Assert.Contains("Attempt failed: bank_declined", html);
        Assert.Contains("Paid with simulator", html);
        Assert.Contains("payment.succeeded", html);
        html = await priya.TextAsync(await priya.GetAsync("/acme/console/developers"));
        Assert.Contains("POST /api/v1/payments", html);
        Assert.Contains("idempotency_conflict", html);
        html = await priya.TextAsync(await priya.GetAsync("/acme/console/payments?q=ACM-20931"));
        Assert.Contains(id, html);

        // A test key can't see live data, and live payments wait until a live method passes its check.
        var liveKey = await CreateKeyAsync(priya, "acme", live: true);
        Assert.StartsWith("sk_live_acme_", liveKey);
        Assert.Equal(HttpStatusCode.NotFound, (await Api(app, liveKey).GetAsync($"/api/v1/payments/{id}")).StatusCode);
        var (live, liveJson) = await PostAsync(Api(app, liveKey), "/api/v1/payments", new { amount = 500 });
        Assert.Equal(HttpStatusCode.BadRequest, live.StatusCode);
        Assert.Equal("live_not_ready", (string?)liveJson["error"]);

        // Wrong or revoked keys.
        Assert.Equal(HttpStatusCode.Unauthorized, (await Api(app, "sk_test_acme_" + new string('x', 32)).GetAsync("/api/v1/payments")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await api.GetAsync("/api/tenant/payments")).StatusCode);
    }

    [Fact]
    public async Task PayLink_UPI_payments_are_confirmed_by_UTR_and_the_merchant()
    {
        var (app, ll, priya) = await StartAsync();
        await using var _ = ll;
        await using var __ = app;

        // Turning UPI on needs a valid VPA and the business-VPA confirmation.
        var r = await priya.FollowAsync(await priya.SubmitAsync("/acme/console/methods", null,
            F(("connector", "upi"), ("mode", "test"), ("action", "enable"), ("test.vpa", "acme"), ("test.payee_name", "Acme Learning"))));
        Assert.Contains("Enter a UPI ID like name@bank", await priya.TextAsync(r));
        r = await priya.FollowAsync(await priya.SubmitAsync("/acme/console/methods", null,
            F(("connector", "upi"), ("mode", "test"), ("action", "enable"), ("test.vpa", "acme@okhdfcbank"), ("test.payee_name", "Acme Learning"), ("test.business_vpa_confirmed", "true"))));
        Assert.Contains("PayLink UPI is on in test", await priya.TextAsync(r));

        // A payment made from the console, with no code.
        r = await priya.FollowAsync(await priya.SubmitAsync("/acme/console/payments", "Create", F(("amount", "1,499.50"), ("description", "October fee"), ("reference", "INV-7"))));
        var id = priya.LastUrl!.AbsolutePath.Split('/').Last();
        Assert.StartsWith("pay_", id);
        Assert.Contains("₹1,499.50", await priya.TextAsync(r));

        // The payer picks UPI and sees a QR code paying acme@okhdfcbank with the payment id in the note.
        var payer = new Browser(app);
        r = await payer.FollowAsync(await payer.SubmitAsync($"/pay/{id}", "Choose", F(("connector", "upi"))));
        var html = await payer.TextAsync(r);
        Assert.Contains("<svg", html);
        Assert.Contains($"upi://pay?pa=acme%40okhdfcbank&amp;pn=Acme%20Learning&amp;am=1499.50&amp;cu=INR", html);
        Assert.Contains($"tn={id}", html);
        var attempt = (await app.Get<PaymentService>().AttemptsAsync(Tenant(app), id)).Single();

        // A wrong UTR is refused; a right one with a screenshot moves the payment to processing.
        r = await payer.FollowAsync(await payer.SubmitAsync($"/pay/{id}", "Utr", F(("attemptId", attempt.Id), ("utr", "12345"))));
        Assert.Contains("12-digit UTR", await payer.TextAsync(r));
        byte[] png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 0, 1, 2, 3];
        r = await payer.FollowAsync(await payer.SubmitWithFileAsync($"/pay/{id}", "Utr", F(("attemptId", attempt.Id), ("utr", "4123 4567 8901")), "screenshot", "paid.png", png, "image/png"));
        html = await payer.TextAsync(r);
        Assert.Contains("Waiting for Acme Learning to confirm", html);
        Assert.Contains("412345678901", html);

        // The same UTR can't be used for a second payment.
        var p2 = (await app.Get<PaymentService>().CreateAsync(Tenant(app), new CreatePaymentRequest(10000, "INR", null, null, null, null, null, null, null, null, null),
            false, null, EventSource.System, "api")).Payment!;
        var payer2 = new Browser(app);
        await payer2.SubmitAsync($"/pay/{p2.Id}", "Choose", F(("connector", "upi")));
        var a2 = (await app.Get<PaymentService>().AttemptsAsync(Tenant(app), p2.Id)).Single();
        r = await payer2.FollowAsync(await payer2.SubmitAsync($"/pay/{p2.Id}", "Utr", F(("attemptId", a2.Id), ("utr", "412345678901"))));
        Assert.Contains("already used for another payment", await payer2.TextAsync(r));

        // The merchant's queue shows it; approving needs a note.
        html = await priya.TextAsync(await priya.GetAsync("/acme/console/upi"));
        Assert.Contains("412345678901", html);
        Assert.Contains("₹1,499.50", html);
        r = await priya.FollowAsync(await priya.SubmitAsync("/acme/console/upi", null, F(("attemptId", attempt.Id), ("decision", "approve"), ("note", ""))));
        Assert.Contains("Add a note", await priya.TextAsync(r));
        var proof = await priya.GetAsync($"/acme/console/payments/{id}?handler=Proof&attemptId={attempt.Id}");
        Assert.Equal(HttpStatusCode.OK, proof.StatusCode);
        Assert.Equal("image/png", proof.Content.Headers.ContentType?.MediaType);
        // Not signed in: no screenshot.
        Assert.Equal(HttpStatusCode.Redirect, (await new Browser(app).GetAsync($"/acme/console/payments/{id}?handler=Proof&attemptId={attempt.Id}")).StatusCode);
        r = await priya.FollowAsync(await priya.SubmitAsync("/acme/console/upi", null, F(("attemptId", attempt.Id), ("decision", "approve"), ("note", "In HDFC statement"))));
        Assert.Contains("approved", await priya.TextAsync(r));

        var paid = (await app.Get<PaymentService>().GetAsync(Tenant(app), id))!;
        Assert.Equal(PaymentStatuses.Succeeded, paid.Status);
        Assert.Equal("upi", paid.Connector);
        Assert.Equal("412345678901", paid.Utr);
        Assert.Contains("Paid", await payer.TextAsync(await payer.GetAsync($"/pay/{id}")));

        // Viewing the screenshot and approving are in the tenant's audit log.
        await using var c = await app.Get<TenantDb>().OpenAsync(Tenant(app));
        var actions = (await c.QueryAsync<string>("SELECT action FROM audit_log;")).ToList();
        Assert.Contains("file.viewed", actions);
        Assert.Contains("upi.approved", actions);
        Assert.Contains("method.enabled", actions);
    }

    [Fact]
    public async Task Risk_rules_holds_cancellation_and_expiry()
    {
        var (app, ll, priya) = await StartAsync();
        await using var _ = ll;
        await using var __ = app;
        await using var hooks = await WebhookReceiver.StartAsync();
        var key = await CreateKeyAsync(priya, "acme");
        var api = Api(app, key);
        await priya.SubmitAsync("/acme/console/developers", "Endpoint", F(("url", hooks.Url)));

        var r = await priya.FollowAsync(await priya.SubmitAsync("/acme/console/risk", "Add", F(("kind", "email_domain"), ("value", "mailinator.com"), ("action", "block"))));
        Assert.Contains("Rule added", await priya.TextAsync(r));
        await priya.SubmitAsync("/acme/console/risk", "Add", F(("kind", "amount_above"), ("value", "1000"), ("action", "review"), ("note", "big orders")));
        r = await priya.FollowAsync(await priya.SubmitAsync("/acme/console/risk", "Add", F(("kind", "ip"), ("value", "not-an-ip"), ("action", "block"))));
        Assert.Contains("Enter an IP address", await priya.TextAsync(r));

        // Blocked: created as failed, with the rule on its journey.
        var (_, blocked) = await PostAsync(api, "/api/v1/payments", new { amount = 5000, customer = new { email = "x@mailinator.com" } });
        Assert.Equal("failed", (string?)blocked["status"]);
        Assert.Equal("risk_blocked", (string?)blocked["failure"]!["reason"]);

        // Held for review: the payer waits until someone releases it with a note.
        var (_, held) = await PostAsync(api, "/api/v1/payments", new { amount = 200000, reference = "BIG-1" });
        var heldId = (string)held["id"]!;
        Assert.Equal("on_hold", (string?)held["status"]);
        var payer = new Browser(app);
        Assert.Contains("needs to review this payment", await payer.TextAsync(await payer.GetAsync($"/pay/{heldId}")));
        r = await priya.FollowAsync(await priya.SubmitAsync($"/acme/console/payments/{heldId}", "Release", F(("note", ""))));
        Assert.Contains("Add a note", await priya.TextAsync(r));
        await priya.SubmitAsync($"/acme/console/payments/{heldId}", "Release", F(("note", "Known customer")));
        Assert.Contains("value=\"simulator\"", await payer.TextAsync(await payer.GetAsync($"/pay/{heldId}")));

        // Cancelled through the API; a second cancel is refused.
        var cancel = await api.PostAsync($"/api/v1/payments/{heldId}/cancel", null);
        Assert.Equal("cancelled", (string?)(await cancel.Content.ReadFromJsonAsync<JsonObject>())!["status"]);
        Assert.Equal(HttpStatusCode.Conflict, (await api.PostAsync($"/api/v1/payments/{heldId}/cancel", null)).StatusCode);

        // Expiry: an unpaid payment past its time expires, and the merchant hears about it.
        var (_, open) = await PostAsync(api, "/api/v1/payments", new { amount = 900, expires_in = 60 });
        var openId = (string)open["id"]!;
        await using (var c = await app.Get<TenantDb>().OpenAsync(Tenant(app)))
            await c.ExecuteAsync("UPDATE payments SET expires_at = '2020-01-01T00:00:00.000Z' WHERE id = @openId;", new { openId });
        await app.Get<PaymentJobs>().RunOnceAsync();
        Assert.Equal("expired", (string?)(await GetJsonAsync(api, $"/api/v1/payments/{openId}"))["status"]);
        var events = hooks.Received.Select(d => d.EventType).ToList();
        Assert.Contains("payment.failed", events);
        Assert.Contains("payment.on_hold", events);
        Assert.Contains("payment.cancelled", events);
        Assert.Contains("payment.expired", events);

        // A failing endpoint is retried later, not dropped.
        hooks.Status = 500;
        await PostAsync(api, "/api/v1/payments/" + openId + "/cancel", new { });
        var (_, again) = await PostAsync(api, "/api/v1/payments", new { amount = 700 });
        await api.PostAsync($"/api/v1/payments/{(string)again["id"]!}/cancel", null);
        await app.Get<PaymentJobs>().RunOnceAsync();
        var failed = (await app.Get<WebhookService>().DeliveriesAsync(Tenant(app))).First();
        Assert.Equal("pending", failed.Status);
        Assert.Equal(1, failed.AttemptCount);
        Assert.Equal(500, failed.LastStatusCode);
        Assert.NotNull(failed.NextAttemptAt);
    }

    [Fact]
    public async Task Tenant_developer_keys_are_limited_to_their_capabilities()
    {
        var (app, ll, priya) = await StartAsync();
        await using var _ = ll;
        await using var __ = app;
        var tdk = await CreateKeyAsync(priya, "acme", "developer", false, TenantCapabilities.PaymentsRead, TenantCapabilities.Methods);
        Assert.StartsWith("tdk_acme_", tdk);
        var api = Api(app, tdk);

        var payment = (await app.Get<PaymentService>().CreateAsync(Tenant(app), new CreatePaymentRequest(2500, null, "R-1", null, null, null, null, null, null, null, null),
            false, null, EventSource.System, "api")).Payment!;
        var list = await GetJsonAsync(api, "/api/tenant/payments?q=R-1");
        Assert.Equal(payment.Id, (string?)list["data"]![0]!["id"]);
        var detail = await GetJsonAsync(api, $"/api/tenant/payments/{payment.Id}");
        Assert.Equal("payment.created", (string?)detail["journey"]![0]!["type"]);

        // UPI settings through the API; secrets never come back.
        var put = await api.PutAsJsonAsync("/api/tenant/methods/upi/test", new { enabled = true, settings = new Dictionary<string, string> { ["vpa"] = "acme@okaxis", ["payee_name"] = "Acme", ["business_vpa_confirmed"] = "true" } });
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        var methods = await GetJsonAsync(api, "/api/tenant/methods");
        var upi = methods["data"]!.AsArray().Single(m => (string?)m!["id"] == "upi")!;
        Assert.True((bool)upi["test"]!["enabled"]!);
        Assert.Equal("acme@okaxis", (string?)upi["test"]!["settings"]!["vpa"]);

        // Missing capabilities and the wrong kind of key are refused.
        var cancel = await api.PostAsJsonAsync($"/api/tenant/payments/{payment.Id}/cancel", new { note = "x" });
        Assert.Equal(HttpStatusCode.Forbidden, cancel.StatusCode);
        Assert.Equal("insufficient_scope", (string?)(await cancel.Content.ReadFromJsonAsync<JsonObject>())!["error"]);
        Assert.Equal(HttpStatusCode.Forbidden, (await api.GetAsync("/api/tenant/webhooks")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await api.GetAsync("/api/v1/payments")).StatusCode);

        // Suspending the tenant stops its keys.
        await app.Get<PlatformRepository>().SetTenantStatusAsync(Tenant(app).Id, "suspended");
        var suspended = await api.GetAsync("/api/tenant/payments");
        Assert.Equal(HttpStatusCode.Forbidden, suspended.StatusCode);
        Assert.Equal("tenant_suspended", (string?)(await suspended.Content.ReadFromJsonAsync<JsonObject>())!["error"]);
    }

    private static TenantRecord Tenant(PayLinkApp app) => app.Get<PlatformRepository>().TenantBySlugAsync("acme").GetAwaiter().GetResult()!;
}
