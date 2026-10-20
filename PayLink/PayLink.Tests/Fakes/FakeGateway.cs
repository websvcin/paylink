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
