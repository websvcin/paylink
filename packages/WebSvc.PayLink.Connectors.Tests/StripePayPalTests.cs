using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using WebSvc.PayLink.Abstractions;
using WebSvc.PayLink.Connector.PayPal;
using WebSvc.PayLink.Connector.Razorpay;
using WebSvc.PayLink.Connector.Stripe;
using Xunit;

namespace WebSvc.PayLink.Connectors.Tests;

/// <summary>Stripe and PayPal against stubbed APIs, and dispute notifications (Stripe, PayPal, Razorpay).</summary>
public sealed class StripePayPalTests
{
    private static AttemptRequest Request(string currency = "USD", long amount = 2599) =>
        new("pay_abc", "att_xyz", amount, currency, "Course fee", new PayerDetails("Asha", "asha@example.com", null),
            "https://paylink.example/pay/pay_abc/return?attempt=att_xyz", new Dictionary<string, string>());

    private static ConnectorConfig Config(PaymentMode mode, params (string, string)[] values) =>
        new(mode, values.ToDictionary(v => v.Item1, v => v.Item2), "https://paylink.example/hooks/acme/x/test");

    private static HttpRequest Http(string body, params (string, string)[] headers)
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
        foreach (var (k, v) in headers) ctx.Request.Headers[k] = v;
        return ctx.Request;
    }

    private static HttpRequest Query(string query) => new DefaultHttpContext { Request = { QueryString = new QueryString(query) } }.Request;

    // ---- Stripe ----

    private static readonly ConnectorConfig StripeTest = Config(PaymentMode.Test, (StripeConnector.SecretKeyField, "sk_test_123"), (StripeConnector.WebhookSecretField, "whsec_abc"));

    [Fact]
    public async Task Stripe_checks_the_key_mode_and_creates_a_checkout_session()
    {
        var stub = new StubHandler((req, body) => req.RequestUri!.AbsolutePath switch
        {
            "/v1/balance" => (HttpStatusCode.OK, """{"object":"balance"}"""),
            "/v1/checkout/sessions" => (HttpStatusCode.OK, """{"id":"cs_test_1","url":"https://checkout.stripe.com/c/pay/cs_test_1"}"""),
            _ => (HttpStatusCode.NotFound, """{"error":{"message":"nope"}}"""),
        });
        var stripe = new StripeConnector(new HttpClient(stub));
        Assert.False((await stripe.TestConnectionAsync(Config(PaymentMode.Live, (StripeConnector.SecretKeyField, "sk_test_123")), default)).Ok);
        Assert.True((await stripe.TestConnectionAsync(StripeTest, default)).Ok);
        Assert.Equal("Bearer sk_test_123", stub.Calls[0].Request.Headers.Authorization!.ToString());

        var next = Assert.IsType<NextAction.Redirect>(await stripe.CreateAttemptAsync(Request(), StripeTest, default));
        Assert.Equal(("cs_test_1", "https://checkout.stripe.com/c/pay/cs_test_1"), (next.GatewayReference, next.Url));
        var (request, form) = stub.Calls[^1];
        Assert.Equal("att_xyz", request.Headers.GetValues("Idempotency-Key").Single());
        var fields = form.Split('&').Select(p => p.Split('=')).ToDictionary(p => Uri.UnescapeDataString(p[0]), p => Uri.UnescapeDataString(p[1].Replace('+', ' ')));
        Assert.Equal(("2599", "usd", "att_xyz", "asha@example.com"),
            (fields["line_items[0][price_data][unit_amount]"], fields["line_items[0][price_data][currency]"], fields["client_reference_id"], fields["customer_email"]));
    }

    [Fact]
    public async Task Stripe_status_and_refunds()
    {
        var stub = new StubHandler((req, body) => req.RequestUri!.AbsolutePath switch
        {
            "/v1/checkout/sessions/cs_paid" => (HttpStatusCode.OK, """{"id":"cs_paid","status":"complete","payment_status":"paid","payment_intent":{"id":"pi_1","payment_method_types":["card"]}}"""),
            "/v1/checkout/sessions/cs_open" => (HttpStatusCode.OK, """{"id":"cs_open","status":"open","payment_status":"unpaid","payment_intent":null}"""),
            "/v1/checkout/sessions/cs_gone" => (HttpStatusCode.OK, """{"id":"cs_gone","status":"expired","payment_status":"unpaid"}"""),
            "/v1/refunds" => (HttpStatusCode.OK, """{"id":"re_1","status":"pending"}"""),
            "/v1/refunds/re_1" => (HttpStatusCode.OK, """{"id":"re_1","status":"succeeded"}"""),
            _ => (HttpStatusCode.NotFound, "{}"),
        });
        var stripe = new StripeConnector(new HttpClient(stub));
        var paid = await stripe.FetchStatusAsync(new AttemptReference("att_xyz", "cs_paid"), StripeTest, default);
        Assert.Equal((AttemptStatus.Succeeded, "pi_1", "card"), (paid.Status, paid.ProviderPaymentId, paid.Method));
        Assert.Equal(AttemptStatus.RequiresAction, (await stripe.FetchStatusAsync(new AttemptReference("a", "cs_open"), StripeTest, default)).Status);
        Assert.Equal(AttemptStatus.Expired, (await stripe.FetchStatusAsync(new AttemptReference("a", "cs_gone"), StripeTest, default)).Status);

        var refund = await stripe.RefundAsync(new RefundRequest("rf_1", new AttemptReference("att_xyz", "cs_paid") { ProviderPaymentId = "pi_1" }, 1000, "USD", "Changed mind"), StripeTest, default);
        Assert.Equal((true, "re_1", RefundState.Pending), (refund.Accepted, refund.GatewayReference, refund.Status));
        Assert.Contains("payment_intent=pi_1", stub.Calls[^1].Body);
        Assert.Equal("rf_1", stub.Calls[^1].Request.Headers.GetValues("Idempotency-Key").Single());
        Assert.Equal(RefundState.Succeeded, (await stripe.FetchRefundAsync("rf_1", "re_1", StripeTest, default)).Status);
    }

    private static (string Body, string Header) Signed(string body, DateTimeOffset at, string secret = "whsec_abc")
    {
        var t = at.ToUnixTimeSeconds().ToString();
        return (body, $"t={t},v1={StripeConnector.Sign(secret, t, body)}");
    }

    [Fact]
    public async Task Stripe_webhooks_are_signed_and_recent()
    {
        var stripe = new StripeConnector(new HttpClient(new StubHandler((_, _) => (HttpStatusCode.OK, "{}"))));
        var completed = """{"type":"checkout.session.completed","data":{"object":{"id":"cs_1","client_reference_id":"att_xyz","status":"complete","payment_status":"paid","payment_intent":"pi_1"}}}""";
        var (body, header) = Signed(completed, DateTimeOffset.UtcNow);
        var ev = await stripe.ParseWebhookAsync(Http(body, ("Stripe-Signature", header)), StripeTest, default);
        Assert.Equal(("cs_1", "att_xyz", AttemptStatus.Succeeded, "pi_1"), (ev.GatewayReference, ev.AttemptId, ev.Status, ev.ProviderPaymentId));

        await Assert.ThrowsAsync<WebhookVerificationException>(() => stripe.ParseWebhookAsync(Http(body, ("Stripe-Signature", header.Replace("v1=", "v1=0"))), StripeTest, default));
        var (_, old) = Signed(completed, DateTimeOffset.UtcNow.AddMinutes(-10));
        await Assert.ThrowsAsync<WebhookVerificationException>(() => stripe.ParseWebhookAsync(Http(completed, ("Stripe-Signature", old)), StripeTest, default));

        (body, header) = Signed("""{"type":"refund.updated","data":{"object":{"id":"re_1","status":"succeeded","payment_intent":"pi_1","metadata":{"paylink_refund":"rf_1"}}}}""", DateTimeOffset.UtcNow);
        ev = await stripe.ParseWebhookAsync(Http(body, ("Stripe-Signature", header)), StripeTest, default);
        Assert.Equal(("re_1", "rf_1", RefundState.Succeeded), (ev.RefundGatewayReference, ev.RefundId, ev.RefundState));

        var due = DateTimeOffset.UtcNow.AddDays(7).ToUnixTimeSeconds();
        (body, header) = Signed("""{"type":"charge.dispute.created","data":{"object":{"id":"dp_1","amount":2599,"currency":"usd","reason":"fraudulent","status":"needs_response","payment_intent":"pi_1","evidence_details":{"due_by":""" + due + "}}}}", DateTimeOffset.UtcNow);
        ev = await stripe.ParseWebhookAsync(Http(body, ("Stripe-Signature", header)), StripeTest, default);
        Assert.Equal(("pi_1", "dp_1", DisputeState.NeedsResponse, 2599L, "USD", "fraudulent"),
            (ev.ProviderPaymentId, ev.Dispute!.GatewayReference, ev.Dispute.State, ev.Dispute.Amount, ev.Dispute.Currency, ev.Dispute.Reason));
        Assert.Equal(due, ev.Dispute.RespondBy!.Value.ToUnixTimeSeconds());
    }

    // ---- PayPal ----

    private static readonly ConnectorConfig PayPalTest = Config(PaymentMode.Test,
        (PayPalConnector.ClientIdField, "client-" + Guid.NewGuid().ToString("N")), (PayPalConnector.ClientSecretField, "secret"), (PayPalConnector.WebhookIdField, "WH-1"));

    private static StubHandler PayPalApi(string orderStatus, Func<string, string>? capture = null) => new((req, body) => (req.Method.Method, req.RequestUri!.AbsolutePath) switch
    {
        ("POST", "/v1/oauth2/token") => (HttpStatusCode.OK, """{"access_token":"A21","expires_in":32400}"""),
        ("POST", "/v2/checkout/orders") => (HttpStatusCode.OK, """{"id":"ORD-1","status":"PAYER_ACTION_REQUIRED","links":[{"rel":"self","href":"x"},{"rel":"payer-action","href":"https://www.sandbox.paypal.com/checkoutnow?token=ORD-1"}]}"""),
        ("GET", "/v2/checkout/orders/ORD-1") => (HttpStatusCode.OK, $$"""{"id":"ORD-1","status":"{{orderStatus}}"}"""),
        ("POST", "/v2/checkout/orders/ORD-1/capture") => (HttpStatusCode.Created, capture?.Invoke(body) ??
            """{"id":"ORD-1","status":"COMPLETED","purchase_units":[{"payments":{"captures":[{"id":"CAP-1","status":"COMPLETED"}]}}]}"""),
        ("POST", "/v2/payments/captures/CAP-1/refund") => (HttpStatusCode.Created, """{"id":"RF-1","status":"COMPLETED"}"""),
        ("POST", "/v1/notifications/verify-webhook-signature") => (HttpStatusCode.OK,
            JsonNode.Parse(body)!["transmission_sig"]!.ToString() == "good" ? """{"verification_status":"SUCCESS"}""" : """{"verification_status":"FAILURE"}"""),
        _ => (HttpStatusCode.NotFound, """{"name":"RESOURCE_NOT_FOUND","message":"nope"}"""),
    });

    [Fact]
    public async Task PayPal_orders_are_captured_when_the_payer_returns()
    {
        var stub = PayPalApi("APPROVED");
        var paypal = new PayPalConnector(new HttpClient(stub));
        Assert.True((await paypal.TestConnectionAsync(PayPalTest, default)).Ok);
        var next = Assert.IsType<NextAction.Redirect>(await paypal.CreateAttemptAsync(Request(), PayPalTest, default));
        Assert.Equal(("ORD-1", "https://www.sandbox.paypal.com/checkoutnow?token=ORD-1"), (next.GatewayReference, next.Url));
        var order = JsonNode.Parse(stub.Calls[^1].Body)!;
        Assert.Equal(("25.99", "USD", "att_xyz"), ((string)order["purchase_units"]![0]!["amount"]!["value"]!, (string)order["purchase_units"]![0]!["amount"]!["currency_code"]!, (string)order["purchase_units"]![0]!["custom_id"]!));
        Assert.Equal("att_xyz", stub.Calls[^1].Request.Headers.GetValues("PayPal-Request-Id").Single());
        Assert.Equal(1, stub.Calls.Count(c => c.Request.RequestUri!.AbsolutePath == "/v1/oauth2/token"));   // token reused

        Assert.Null(await paypal.ParseReturnAsync(Query("?token=OTHER"), new AttemptReference("att_xyz", "ORD-1"), PayPalTest, default));
        var ev = await paypal.ParseReturnAsync(Query("?token=ORD-1&PayerID=P1"), new AttemptReference("att_xyz", "ORD-1"), PayPalTest, default);
        Assert.Equal((AttemptStatus.Succeeded, "CAP-1"), (ev!.Status, ev.ProviderPaymentId));
        Assert.Equal("capture-att_xyz", stub.Calls.Single(c => c.Request.RequestUri!.AbsolutePath.EndsWith("/capture")).Request.Headers.GetValues("PayPal-Request-Id").Single());

        var refund = await paypal.RefundAsync(new RefundRequest("rf_1", new AttemptReference("att_xyz", "ORD-1") { ProviderPaymentId = "CAP-1" }, 1000, "USD", "Sorry"), PayPalTest, default);
        Assert.Equal((true, "RF-1", RefundState.Succeeded), (refund.Accepted, refund.GatewayReference, refund.Status));
        Assert.Equal(("10.00", "rf_1"), ((string)JsonNode.Parse(stub.Calls[^1].Body)!["amount"]!["value"]!, (string)JsonNode.Parse(stub.Calls[^1].Body)!["invoice_id"]!));

        Assert.Equal(AttemptStatus.RequiresAction, (await new PayPalConnector(new HttpClient(PayPalApi("PAYER_ACTION_REQUIRED"))).FetchStatusAsync(new AttemptReference("a", "ORD-1"), PayPalTest, default)).Status);
        Assert.Equal("10", PayPalConnector.Value(10, "JPY"));
        Assert.Equal(2599, PayPalConnector.Minor("25.99", "USD"));
    }

    [Fact]
    public async Task PayPal_webhooks_are_verified_by_PayPal()
    {
        var paypal = new PayPalConnector(new HttpClient(PayPalApi("COMPLETED")));
        var body = """{"id":"WH-EV-1","event_type":"PAYMENT.CAPTURE.COMPLETED","resource":{"id":"CAP-1","status":"COMPLETED","custom_id":"att_xyz","supplementary_data":{"related_ids":{"order_id":"ORD-1"}}}}""";
        (string, string)[] Headers(string sig) => [("PAYPAL-AUTH-ALGO", "SHA256withRSA"), ("PAYPAL-CERT-URL", "https://api.paypal.com/cert"), ("PAYPAL-TRANSMISSION-ID", "t1"),
            ("PAYPAL-TRANSMISSION-SIG", sig), ("PAYPAL-TRANSMISSION-TIME", "2026-10-20T10:00:00Z")];
        await Assert.ThrowsAsync<WebhookVerificationException>(() => paypal.ParseWebhookAsync(Http(body, Headers("bad")), PayPalTest, default));
        var ev = await paypal.ParseWebhookAsync(Http(body, Headers("good")), PayPalTest, default);
        Assert.Equal(("ORD-1", "att_xyz", AttemptStatus.Succeeded, "CAP-1"), (ev.GatewayReference, ev.AttemptId, ev.Status, ev.ProviderPaymentId));

        var dispute = """{"event_type":"CUSTOMER.DISPUTE.RESOLVED","resource":{"dispute_id":"PP-D-1","reason":"MERCHANDISE_OR_SERVICE_NOT_RECEIVED","status":"RESOLVED","dispute_outcome":{"outcome_code":"RESOLVED_SELLER_FAVOUR"},"dispute_amount":{"currency_code":"USD","value":"25.99"},"disputed_transactions":[{"seller_transaction_id":"CAP-1"}]}}""";
        ev = await paypal.ParseWebhookAsync(Http(dispute, Headers("good")), PayPalTest, default);
        Assert.Equal(("CAP-1", "PP-D-1", DisputeState.Won, 2599L), (ev.ProviderPaymentId, ev.Dispute!.GatewayReference, ev.Dispute.State, ev.Dispute.Amount));
    }

    [Fact]
    public async Task Razorpay_reports_disputes()
    {
        var razorpay = new RazorpayConnector(new HttpClient(new StubHandler((_, _) => (HttpStatusCode.OK, "{}"))));
        var config = Config(PaymentMode.Test, (RazorpayConnector.KeyIdField, "rzp_test_x"), (RazorpayConnector.KeySecretField, "s"), (RazorpayConnector.WebhookSecretField, "whs"));
        var body = """{"event":"payment.dispute.created","payload":{"payment":{"entity":{"id":"pay_R1"}},"dispute":{"entity":{"id":"disp_1","payment_id":"pay_R1","amount":149900,"currency":"INR","reason_code":"chargeback","status":"open","respond_by":1793000000}}}}""";
        var sig = Convert.ToHexString(new HMACSHA256(Encoding.UTF8.GetBytes("whs")).ComputeHash(Encoding.UTF8.GetBytes(body))).ToLowerInvariant();
        var ev = await razorpay.ParseWebhookAsync(Http(body, ("X-Razorpay-Signature", sig)), config, default);
        Assert.Equal(("pay_R1", "disp_1", DisputeState.NeedsResponse, 149900L), (ev.ProviderPaymentId, ev.Dispute!.GatewayReference, ev.Dispute.State, ev.Dispute.Amount));
    }
}
