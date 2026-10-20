using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;

namespace PayLink.Tests.Fakes;

/// <summary>A tiny Razorpay: payment links, their status, refunds. Enough to drive PayLink end to end.</summary>
public sealed class FakeRazorpay : HttpMessageHandler
{
    public ConcurrentDictionary<string, string> LinkStatus { get; } = new();
    public string RefundStatus { get; set; } = "pending";
    public List<string> Calls { get; } = [];
    /// <summary>When true, creating a payment link fails with a 500, as a gateway outage would.</summary>
    public bool Down { get; set; }
    private int _links;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var path = request.RequestUri!.AbsolutePath;
        var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
        lock (Calls) Calls.Add($"{request.Method} {path}");
        if (path == "/v1/payments") return Json("""{"items":[]}""");
        if (path == "/v1/payment_links" && request.Method == HttpMethod.Post)
        {
            if (Down) return new HttpResponseMessage(HttpStatusCode.InternalServerError) { Content = new StringContent("""{"error":{"code":"SERVER_ERROR","description":"Something went wrong"}}""") };
            var id = "plink_T" + Interlocked.Increment(ref _links);
            LinkStatus[id] = "created";
            return Json($$"""{"id":"{{id}}","short_url":"https://rzp.io/i/{{id}}","status":"created"}""");
        }
        if (path.StartsWith("/v1/payment_links/", StringComparison.Ordinal))
        {
            var id = path[18..];
            var status = LinkStatus.GetValueOrDefault(id, "created");
            var payments = status == "paid" ? $$"""[{"payment_id":"pay_{{id}}","method":"upi"}]""" : "[]";
            return Json($$"""{"id":"{{id}}","status":"{{status}}","payments":{{payments}}}""");
        }
        if (path.EndsWith("/refund", StringComparison.Ordinal))
            return Json($$"""{"id":"rfnd_{{Guid.NewGuid():N}}","status":"{{RefundStatus}}","receipt":"{{(string?)JsonNode.Parse(body)!["receipt"]}}"}""");
        return new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("""{"error":{"code":"NOT_FOUND","description":"no such path"}}""") };
    }

    private static HttpResponseMessage Json(string text) => new(HttpStatusCode.OK) { Content = new StringContent(text, Encoding.UTF8, "application/json") };
}

/// <summary>Stripe's API, enough for Checkout Sessions, status and refunds.</summary>
public sealed class FakeStripe : HttpMessageHandler
{
    public ConcurrentDictionary<string, string> SessionStatus { get; } = new();
    public List<string> Calls { get; } = [];
    private int _sessions;

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(Answer(request));

    private HttpResponseMessage Answer(HttpRequestMessage request)
    {
        var path = request.RequestUri!.AbsolutePath;
        lock (Calls) Calls.Add($"{request.Method} {path}");
        if (path == "/v1/balance") return Json("""{"object":"balance"}""");
        if (path == "/v1/checkout/sessions" && request.Method == HttpMethod.Post)
        {
            var id = "cs_test_" + Interlocked.Increment(ref _sessions);
            SessionStatus[id] = "open";
            return Json($$"""{"id":"{{id}}","url":"https://checkout.stripe.com/c/pay/{{id}}"}""");
        }
        if (path.StartsWith("/v1/checkout/sessions/", StringComparison.Ordinal))
        {
            var id = path["/v1/checkout/sessions/".Length..];
            return SessionStatus.GetValueOrDefault(id) == "paid"
                ? Json("{\"id\":\"" + id + "\",\"status\":\"complete\",\"payment_status\":\"paid\",\"payment_intent\":{\"id\":\"pi_" + id + "\",\"payment_method_types\":[\"card\"]}}")
                : Json($$"""{"id":"{{id}}","status":"open","payment_status":"unpaid","payment_intent":null}""");
        }
        if (path == "/v1/refunds") return Json("""{"id":"re_1","status":"succeeded"}""");
        return new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("""{"error":{"message":"no such path"}}""") };
    }

    private static HttpResponseMessage Json(string text) => new(HttpStatusCode.OK) { Content = new StringContent(text, System.Text.Encoding.UTF8, "application/json") };
}
