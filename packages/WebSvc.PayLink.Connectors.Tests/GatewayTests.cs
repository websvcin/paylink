using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;
using WebSvc.PayLink.Abstractions;
using WebSvc.PayLink.Connector.CCAvenue;
using WebSvc.PayLink.Connector.PayU;
using WebSvc.PayLink.Connector.PhonePe;
using WebSvc.PayLink.Connector.Razorpay;
using Xunit;

namespace WebSvc.PayLink.Connectors.Tests;

/// <summary>Answers gateway calls from a function and records them.</summary>
public sealed class StubHandler(Func<HttpRequestMessage, string, (HttpStatusCode, string)> answer) : HttpMessageHandler
{
    public List<(HttpRequestMessage Request, string Body)> Calls { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
        Calls.Add((request, body));
        var (status, text) = answer(request, body);
        return new HttpResponseMessage(status) { Content = new StringContent(text, Encoding.UTF8, "application/json") };
    }
}

public sealed class GatewayTests
{
    private static AttemptRequest Request() =>
        new("pay_abc", "att_xyz", 149900, "INR", "Course fee", new PayerDetails("Rahul Verma", "rahul@example.com", "+919812345678"),
            "https://paylink.example/pay/pay_abc/return?attempt=att_xyz", new Dictionary<string, string>());

    private static ConnectorConfig Config(PaymentMode mode, params (string, string)[] values) =>
        new(mode, values.ToDictionary(v => v.Item1, v => v.Item2), "https://paylink.example/hooks/acme/x/test");

    private static HttpRequest Http(string? body = null, Dictionary<string, string>? form = null, string? query = null, params (string, string)[] headers)
    {
        var ctx = new DefaultHttpContext();
        if (form is not null)
        {
            ctx.Request.ContentType = "application/x-www-form-urlencoded";
            ctx.Request.Form = new FormCollection(form.ToDictionary(kv => kv.Key, kv => new StringValues(kv.Value)));
        }
        if (body is not null) ctx.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
        if (query is not null) ctx.Request.QueryString = new QueryString(query);
        foreach (var (k, v) in headers) ctx.Request.Headers[k] = v;
        return ctx.Request;
    }

    private static string Hmac(string secret, string text) =>
        Convert.ToHexString(new HMACSHA256(Encoding.UTF8.GetBytes(secret)).ComputeHash(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    // ---------- Razorpay ----------

    [Fact]
    public async Task Razorpay_creates_a_payment_link_and_redirects_to_it()
    {
        var stub = new StubHandler((r, _) => (HttpStatusCode.OK, """{"id":"plink_1","short_url":"https://rzp.io/i/abc"}"""));
        var rzp = new RazorpayConnector(new HttpClient(stub));
        var next = await rzp.CreateAttemptAsync(Request(), Config(PaymentMode.Test, ("key_id", "rzp_test_k"), ("key_secret", "s")), default);
        var redirect = Assert.IsType<NextAction.Redirect>(next);
        Assert.Equal("https://rzp.io/i/abc", redirect.Url);
        Assert.Equal("plink_1", redirect.GatewayReference);
        var (req, body) = stub.Calls.Single();
        Assert.Equal("https://api.razorpay.com/v1/payment_links", req.RequestUri!.ToString());
        Assert.Equal("Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("rzp_test_k:s")), req.Headers.Authorization!.ToString());
        var json = JsonNode.Parse(body)!;
        Assert.Equal(149900, (long)json["amount"]!);
        Assert.Equal("att_xyz", (string?)json["reference_id"]);
        Assert.Equal("https://paylink.example/pay/pay_abc/return?attempt=att_xyz", (string?)json["callback_url"]);
        Assert.Equal("+919812345678", (string?)json["customer"]!["contact"]);
    }

    [Fact]
    public async Task Razorpay_checks_the_key_matches_the_mode()
    {
        var rzp = new RazorpayConnector(new HttpClient(new StubHandler((_, _) => (HttpStatusCode.OK, "{}"))));
        Assert.False((await rzp.TestConnectionAsync(Config(PaymentMode.Live, ("key_id", "rzp_test_k"), ("key_secret", "s")), default)).Ok);
        Assert.True((await rzp.TestConnectionAsync(Config(PaymentMode.Live, ("key_id", "rzp_live_k"), ("key_secret", "s")), default)).Ok);
    }

    [Fact]
    public async Task Razorpay_callbacks_and_webhooks_must_be_signed()
    {
        var rzp = new RazorpayConnector(new HttpClient(new StubHandler((_, _) => (HttpStatusCode.OK, "{}"))));
        var config = Config(PaymentMode.Test, ("key_id", "rzp_test_k"), ("key_secret", "secret"), ("webhook_secret", "whs"));
        var attempt = new AttemptReference("att_xyz", "plink_1");
        var sig = Hmac("secret", "plink_1|att_xyz|paid|pay_99");
        var good = await rzp.ParseReturnAsync(Http(query: $"?razorpay_payment_id=pay_99&razorpay_payment_link_id=plink_1&razorpay_payment_link_reference_id=att_xyz&razorpay_payment_link_status=paid&razorpay_signature={sig}"), attempt, config, default);
        Assert.Equal(AttemptStatus.Succeeded, good!.Status);
        Assert.Equal("pay_99", good.ProviderPaymentId);
        var forged = await rzp.ParseReturnAsync(Http(query: "?razorpay_payment_id=pay_99&razorpay_payment_link_id=plink_1&razorpay_payment_link_reference_id=att_xyz&razorpay_payment_link_status=paid&razorpay_signature=00"), attempt, config, default);
        Assert.Null(forged);

        var body = """{"event":"payment_link.paid","payload":{"payment_link":{"entity":{"id":"plink_1","reference_id":"att_xyz"}},"payment":{"entity":{"id":"pay_99"}}}}""";
        var ev = await rzp.ParseWebhookAsync(Http(body, headers: ("X-Razorpay-Signature", Hmac("whs", body))), config, default);
        Assert.Equal(AttemptStatus.Succeeded, ev.Status);
        Assert.Equal("att_xyz", ev.AttemptId);
        await Assert.ThrowsAsync<WebhookVerificationException>(() => rzp.ParseWebhookAsync(Http(body, headers: ("X-Razorpay-Signature", Hmac("other", body))), config, default));

        var refund = """{"event":"refund.processed","payload":{"refund":{"entity":{"id":"rfnd_1","payment_id":"pay_99","receipt":"rf_abc","status":"processed"}}}}""";
        var r = await rzp.ParseWebhookAsync(Http(refund, headers: ("X-Razorpay-Signature", Hmac("whs", refund))), config, default);
        Assert.Equal(RefundState.Succeeded, r.RefundState);
        Assert.Equal("rf_abc", r.RefundId);
    }

    [Fact]
    public async Task Razorpay_refunds_the_gateway_payment()
    {
        var stub = new StubHandler((_, _) => (HttpStatusCode.OK, """{"id":"rfnd_1","status":"processed"}"""));
        var rzp = new RazorpayConnector(new HttpClient(stub));
        var result = await rzp.RefundAsync(new RefundRequest("rf_1", new AttemptReference("att_xyz", "plink_1") { ProviderPaymentId = "pay_99" }, 5000, "INR", "Returned"),
            Config(PaymentMode.Test, ("key_id", "rzp_test_k"), ("key_secret", "s")), default);
        Assert.True(result.Accepted);
        Assert.Equal(RefundState.Succeeded, result.Status);
        Assert.Equal("https://api.razorpay.com/v1/payments/pay_99/refund", stub.Calls.Single().Request.RequestUri!.ToString());
        Assert.Equal(5000, (long)JsonNode.Parse(stub.Calls.Single().Body)!["amount"]!);
    }

    // ---------- PhonePe ----------

    [Fact]
    public async Task PhonePe_v2_gets_an_oauth_token_then_starts_checkout()
    {
        var stub = new StubHandler((r, _) => r.RequestUri!.AbsolutePath.EndsWith("/oauth/token")
            ? (HttpStatusCode.OK, """{"access_token":"tok","expires_at":4102444800,"token_type":"O-Bearer"}""")
            : (HttpStatusCode.OK, """{"orderId":"OMO1","state":"PENDING","redirectUrl":"https://mercury.phonepe.com/pay/1"}"""));
        var pp = new PhonePeConnector(new HttpClient(stub));
        var config = Config(PaymentMode.Test, ("api_version", "v2"), ("client_id", "cid-" + Guid.NewGuid()), ("client_secret", "cs"), ("client_version", "1"));
        var next = Assert.IsType<NextAction.Redirect>(await pp.CreateAttemptAsync(Request(), config, default));
        Assert.Equal("https://mercury.phonepe.com/pay/1", next.Url);
        Assert.Equal("https://api-preprod.phonepe.com/apis/pg-sandbox/v1/oauth/token", stub.Calls[0].Request.RequestUri!.ToString());
        Assert.Contains("grant_type=client_credentials", stub.Calls[0].Body);
        var pay = stub.Calls[1];
        Assert.Equal("https://api-preprod.phonepe.com/apis/pg-sandbox/checkout/v2/pay", pay.Request.RequestUri!.ToString());
        Assert.Equal("O-Bearer tok", pay.Request.Headers.Authorization!.ToString());
        Assert.Equal("att_xyz", (string?)JsonNode.Parse(pay.Body)!["merchantOrderId"]);
    }

    [Fact]
    public async Task PhonePe_v2_webhooks_carry_the_hashed_username_and_password()
    {
        var pp = new PhonePeConnector(new HttpClient(new StubHandler((_, _) => (HttpStatusCode.OK, "{}"))));
        var config = Config(PaymentMode.Test, ("api_version", "v2"), ("webhook_username", "u"), ("webhook_password", "p"));
        var auth = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("u:p"))).ToLowerInvariant();
        var body = """{"event":"checkout.order.completed","payload":{"merchantOrderId":"att_xyz","orderId":"OMO1","state":"COMPLETED","paymentDetails":[{"transactionId":"T1"}]}}""";
        var ev = await pp.ParseWebhookAsync(Http(body, headers: ("Authorization", auth)), config, default);
        Assert.Equal(AttemptStatus.Succeeded, ev.Status);
        Assert.Equal("T1", ev.ProviderPaymentId);
        await Assert.ThrowsAsync<WebhookVerificationException>(() => pp.ParseWebhookAsync(Http(body, headers: ("Authorization", "nope")), config, default));
    }

    [Fact]
    public async Task PhonePe_v1_signs_with_the_salt_key()
    {
        var stub = new StubHandler((_, _) => (HttpStatusCode.OK, """{"success":true,"code":"PAYMENT_INITIATED","data":{"instrumentResponse":{"redirectInfo":{"url":"https://phonepe/pay"}}}}"""));
        var pp = new PhonePeConnector(new HttpClient(stub));
        var config = Config(PaymentMode.Test, ("api_version", "v1"), ("merchant_id", "M1"), ("salt_key", "salt"), ("salt_index", "1"));
        Assert.IsType<NextAction.Redirect>(await pp.CreateAttemptAsync(Request(), config, default));
        var (req, body) = stub.Calls.Single();
        var b64 = (string)JsonNode.Parse(body)!["request"]!;
        var expected = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(b64 + "/pg/v1/pay" + "salt"))).ToLowerInvariant() + "###1";
        Assert.Equal(expected, req.Headers.GetValues("X-VERIFY").Single());
        Assert.Equal("att_xyz", (string?)JsonNode.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(b64)))!["merchantTransactionId"]);
    }

    // ---------- PayU ----------

    [Fact]
    public async Task PayU_posts_a_hashed_form_and_accepts_only_a_correct_reverse_hash()
    {
        var payu = new PayUConnector(new HttpClient(new StubHandler((_, _) => (HttpStatusCode.OK, "{}"))));
        var config = Config(PaymentMode.Test, ("merchant_key", "KEY"), ("merchant_salt", "SALT"));
        var form = Assert.IsType<NextAction.PostForm>(await payu.CreateAttemptAsync(Request(), config, default));
        Assert.Equal("https://test.payu.in/_payment", form.Url);
        var f = form.Fields;
        Assert.Equal("1499.00", f["amount"]);
        var expected = PayUConnector.Sha512($"KEY|att_xyz|1499.00|Course fee|Rahul Verma|rahul@example.com|pay_abc||||||||||SALT");
        Assert.Equal(expected, f["hash"]);

        var back = new Dictionary<string, string>
        {
            ["key"] = "KEY", ["txnid"] = "att_xyz", ["amount"] = "1499.00", ["productinfo"] = "Course fee", ["firstname"] = "Rahul Verma",
            ["email"] = "rahul@example.com", ["udf1"] = "pay_abc", ["status"] = "success", ["mihpayid"] = "403993715",
        };
        back["hash"] = PayUConnector.Sha512("SALT|success||||||||||pay_abc|rahul@example.com|Rahul Verma|Course fee|1499.00|att_xyz|KEY");
        var ok = await payu.ParseReturnAsync(Http(form: back), new AttemptReference("att_xyz", "att_xyz"), config, default);
        Assert.Equal(AttemptStatus.Succeeded, ok!.Status);
        Assert.Equal("403993715", ok.ProviderPaymentId);

        back["amount"] = "1.00";   // tampered
        Assert.Null(await payu.ParseReturnAsync(Http(form: back), new AttemptReference("att_xyz", "att_xyz"), config, default));
    }

    // ---------- CCAvenue ----------

    [Fact]
    public void CCAvenue_encryption_uses_md5_of_the_working_key_and_a_fixed_iv()
    {
        var hex = CCAvenueConnector.Encrypt("order_id=att_xyz&order_status=Success", "WK123");
        Assert.Equal("order_id=att_xyz&order_status=Success", CCAvenueConnector.Decrypt(hex, "WK123"));
        // The same key and IV as CCAvenue's own kits: AES-128-CBC, key = MD5(working key), IV = 00..0F.
        using var aes = Aes.Create();
        aes.Key = MD5.HashData(Encoding.UTF8.GetBytes("WK123"));
        var iv = Enumerable.Range(0, 16).Select(i => (byte)i).ToArray();
        Assert.Equal(Convert.ToHexString(aes.EncryptCbc(Encoding.UTF8.GetBytes("abc"), iv)).ToLowerInvariant(), CCAvenueConnector.Encrypt("abc", "WK123"));
    }

    [Fact]
    public async Task CCAvenue_reads_only_responses_made_with_the_working_key()
    {
        var cc = new CCAvenueConnector(new HttpClient(new StubHandler((_, _) => (HttpStatusCode.OK, ""))));
        var config = Config(PaymentMode.Test, ("merchant_id", "1"), ("access_code", "AC"), ("working_key", "WK123"));
        var form = Assert.IsType<NextAction.PostForm>(await cc.CreateAttemptAsync(Request(), config, default));
        Assert.Contains("order_id=att_xyz", CCAvenueConnector.Decrypt(form.Fields["encRequest"], "WK123"));
        Assert.Contains("amount=1499.00", CCAvenueConnector.Decrypt(form.Fields["encRequest"], "WK123"));

        var enc = CCAvenueConnector.Encrypt("order_id=att_xyz&tracking_id=T9&order_status=Success&amount=1499.00", "WK123");
        var ev = await cc.ParseReturnAsync(Http(form: new() { ["encResp"] = enc }), new AttemptReference("att_xyz", "att_xyz"), config, default);
        Assert.Equal(AttemptStatus.Succeeded, ev!.Status);
        Assert.Equal("T9", ev.ProviderPaymentId);
        var other = CCAvenueConnector.Encrypt("order_id=att_xyz&order_status=Success", "SOMEONE-ELSE");
        Assert.Null(await cc.ParseReturnAsync(Http(form: new() { ["encResp"] = other }), new AttemptReference("att_xyz", "att_xyz"), config, default));
        var aborted = CCAvenueConnector.Encrypt("order_id=att_xyz&order_status=Aborted", "WK123");
        Assert.Equal(AttemptStatus.Cancelled, (await cc.ParseReturnAsync(Http(form: new() { ["encResp"] = aborted }), new AttemptReference("att_xyz", "att_xyz"), config, default))!.Status);
    }
}
