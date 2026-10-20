using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using WebSvc.PayLink.Abstractions;

namespace WebSvc.PayLink.Connector.Razorpay;

/// <summary>
/// Razorpay through Payment Links: PayLink creates a link for each attempt and sends the payer to its short URL; Razorpay
/// returns the payer with a signed query string and notifies <c>/hooks/{tenant}/razorpay/{mode}</c> (X-Razorpay-Signature).
/// Test and Live use the same API host; the key decides the mode (rzp_test_… / rzp_live_…).
/// </summary>
public sealed class RazorpayConnector(HttpClient http) : IPaymentConnector, IPayerReturnHandler, IRefundStatusFetcher
{
    public const string Id = "razorpay";
    public const string KeyIdField = "key_id";
    public const string KeySecretField = "key_secret";
    public const string WebhookSecretField = "webhook_secret";
    public static string ApiBase { get; set; } = "https://api.razorpay.com";

    public ConnectorManifest Manifest { get; } = new(
        Id, "Razorpay", "0.2.0", AbstractionsMajorVersion: 0,
        Methods: [PaymentMethodKind.Upi, PaymentMethodKind.Card, PaymentMethodKind.NetBanking, PaymentMethodKind.Wallet],
        Currencies: ["INR"],
        Capabilities: new ConnectorCapabilities(Refunds: true, PartialRefunds: true, Webhooks: true, StatusPolling: true),
        ConfigSchema:
        [
            new ConfigField(KeyIdField, "Key ID", ConfigFieldType.Text, Required: true, Help: "Razorpay Dashboard → Account & Settings → API keys. Test keys start with rzp_test_, live keys with rzp_live_."),
            new ConfigField(KeySecretField, "Key secret", ConfigFieldType.Secret, Required: true),
            new ConfigField(WebhookSecretField, "Webhook secret", ConfigFieldType.Secret, Required: false,
                Help: "Razorpay Dashboard → Webhooks: add the webhook URL shown here with events payment_link.paid, payment_link.expired, payment_link.cancelled, refund.processed, refund.failed and payment.dispute.*, and the same secret."),
        ]);

    private HttpRequestMessage Request(HttpMethod method, string path, ConnectorConfig config, object? body = null)
    {
        var r = new HttpRequestMessage(method, ApiBase + path);
        r.Headers.Authorization = new AuthenticationHeaderValue("Basic",
            Convert.ToBase64String(Encoding.UTF8.GetBytes($"{config.Values.GetValueOrDefault(KeyIdField)}:{config.Values.GetValueOrDefault(KeySecretField)}")));
        if (body is not null) r.Content = JsonContent.Create(body);
        return r;
    }

    private async Task<(bool Ok, JsonNode? Json, string Error)> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        using (request)
        using (var response = await http.SendAsync(request, ct))
        {
            var text = await response.Content.ReadAsStringAsync(ct);
            JsonNode? json = null;
            try { json = string.IsNullOrWhiteSpace(text) ? null : JsonNode.Parse(text); } catch (System.Text.Json.JsonException) { }
            if (response.IsSuccessStatusCode) return (true, json, "");
            var error = (string?)json?["error"]?["description"] ?? $"Razorpay answered {(int)response.StatusCode}.";
            return (false, json, error);
        }
    }

    public async Task<ConnectionCheck> TestConnectionAsync(ConnectorConfig config, CancellationToken cancellationToken)
    {
        var keyId = config.Values.GetValueOrDefault(KeyIdField) ?? "";
        var expected = config.Mode == PaymentMode.Live ? "rzp_live_" : "rzp_test_";
        if (!keyId.StartsWith(expected, StringComparison.Ordinal))
            return new(false, $"{(config.Mode == PaymentMode.Live ? "Live" : "Test")} settings need a key starting with {expected}.");
        var (ok, _, error) = await SendAsync(Request(HttpMethod.Get, "/v1/payments?count=1", config), cancellationToken);
        if (!ok) return new(false, "Razorpay refused the keys: " + error);
        return new(true, string.IsNullOrEmpty(config.Values.GetValueOrDefault(WebhookSecretField))
            ? "Connected. Add a webhook secret too, so payments are confirmed even if the payer closes the browser."
            : "Connected to Razorpay.");
    }

    public async Task<NextAction> CreateAttemptAsync(AttemptRequest request, ConnectorConfig config, CancellationToken cancellationToken)
    {
        var customer = new Dictionary<string, string>();
        if (request.Payer.Name is { Length: > 0 } n) customer["name"] = n;
        if (request.Payer.Email is { Length: > 0 } e) customer["email"] = e;
        if (request.Payer.Phone is { Length: > 0 } p) customer["contact"] = p;
        var body = new Dictionary<string, object>
        {
            ["amount"] = request.Amount,
            ["currency"] = request.Currency,
            ["reference_id"] = request.AttemptId,
            ["description"] = Truncate(string.IsNullOrWhiteSpace(request.Description) ? request.PaymentId : request.Description, 2048),
            ["customer"] = customer,
            ["notify"] = new { sms = false, email = false },
            ["reminder_enable"] = false,
            ["notes"] = new { paylink_payment = request.PaymentId, paylink_attempt = request.AttemptId },
            ["callback_url"] = request.ReturnUrl,
            ["callback_method"] = "get",
        };
        var (ok, json, error) = await SendAsync(Request(HttpMethod.Post, "/v1/payment_links", config, body), cancellationToken);
        if (!ok || (string?)json?["short_url"] is not { } url)
            return new NextAction.Failed("", FailureReason.GatewayError, (string?)json?["error"]?["code"] ?? "create_failed", error);
        return new NextAction.Redirect((string)json!["id"]!, url);
    }

    /// <summary>Razorpay's callback: razorpay_signature = HMAC-SHA256(key secret, link id | reference id | link status | payment id).</summary>
    public Task<GatewayEvent?> ParseReturnAsync(HttpRequest request, AttemptReference attempt, ConnectorConfig config, CancellationToken cancellationToken)
    {
        var q = request.Query;
        string Q(string k) => q[k].ToString();
        var linkId = Q("razorpay_payment_link_id");
        var payload = $"{linkId}|{Q("razorpay_payment_link_reference_id")}|{Q("razorpay_payment_link_status")}|{Q("razorpay_payment_id")}";
        if (linkId.Length == 0 || linkId != attempt.GatewayReference
            || !FixedEquals(Hmac(config.Values.GetValueOrDefault(KeySecretField) ?? "", payload), Q("razorpay_signature")))
            return Task.FromResult<GatewayEvent?>(null);   // unsigned or not this attempt: PayLink checks the status instead
        var status = Q("razorpay_payment_link_status") == "paid" ? AttemptStatus.Succeeded : AttemptStatus.Processing;
        return Task.FromResult<GatewayEvent?>(new GatewayEvent(linkId, attempt.AttemptId, status, FailureReason.None,
            Q("razorpay_payment_link_status"), "Razorpay callback", request.QueryString.Value ?? "") { ProviderPaymentId = Q("razorpay_payment_id").NullIfEmpty() });
    }

    public async Task<GatewayEvent> ParseWebhookAsync(HttpRequest request, ConnectorConfig config, CancellationToken cancellationToken)
    {
        var secret = config.Values.GetValueOrDefault(WebhookSecretField);
        if (string.IsNullOrEmpty(secret)) throw new WebhookVerificationException("No webhook secret is set for Razorpay.");
        request.Body.Position = 0;
        var raw = await new StreamReader(request.Body).ReadToEndAsync(cancellationToken);
        if (!FixedEquals(Hmac(secret, raw), request.Headers["X-Razorpay-Signature"].ToString()))
            throw new WebhookVerificationException("X-Razorpay-Signature doesn't match.");
        var json = JsonNode.Parse(raw)!;
        var ev = (string?)json["event"] ?? "";
        var link = json["payload"]?["payment_link"]?["entity"];
        var payment = json["payload"]?["payment"]?["entity"];
        var refund = json["payload"]?["refund"]?["entity"];
        if (ev.StartsWith("payment.dispute.", StringComparison.Ordinal) && json["payload"]?["dispute"]?["entity"] is { } dispute)
        {
            var state = (string?)dispute["status"] switch
            {
                "won" => DisputeState.Won,
                "lost" => DisputeState.Lost,
                "closed" => DisputeState.Closed,
                "under_review" => DisputeState.UnderReview,
                _ => DisputeState.NeedsResponse,
            };
            var respondBy = (long?)dispute["respond_by"];
            return new GatewayEvent("", null, AttemptStatus.Succeeded, FailureReason.None, ev, (string?)dispute["reason_description"] ?? ev, raw)
            {
                ProviderPaymentId = (string?)dispute["payment_id"],
                Dispute = new DisputeInfo((string?)dispute["id"] ?? "", state, (long?)dispute["amount"] ?? 0, (string?)dispute["currency"] ?? "INR", (string?)dispute["reason_code"])
                {
                    RespondBy = respondBy is { } t ? DateTimeOffset.FromUnixTimeSeconds(t) : null,
                },
            };
        }
        if (ev.StartsWith("refund.", StringComparison.Ordinal) && refund is not null)
        {
            var state = ev == "refund.processed" ? RefundState.Succeeded : ev == "refund.failed" ? RefundState.Failed : RefundState.Pending;
            return new GatewayEvent("", null, AttemptStatus.Succeeded, FailureReason.None, ev, (string?)refund["status"] ?? ev, raw)
            {
                RefundGatewayReference = (string?)refund["id"], RefundId = (string?)refund["receipt"], RefundState = state,
                ProviderPaymentId = (string?)refund["payment_id"],
            };
        }
        var (status, reason) = ev switch
        {
            "payment_link.paid" => (AttemptStatus.Succeeded, FailureReason.None),
            "payment_link.expired" => (AttemptStatus.Expired, FailureReason.Expired),
            "payment_link.cancelled" => (AttemptStatus.Cancelled, FailureReason.PayerCancelled),
            "payment.failed" => (AttemptStatus.RequiresAction, FailureReason.BankDeclined),   // the payer can retry on the same link
            _ => (AttemptStatus.Processing, FailureReason.None),
        };
        return new GatewayEvent((string?)link?["id"] ?? "", (string?)link?["reference_id"], status, reason, ev,
            (string?)payment?["error_description"] ?? ev, raw)
        { ProviderPaymentId = (string?)payment?["id"] };
    }

    public async Task<AttemptStatusResult> FetchStatusAsync(AttemptReference attempt, ConnectorConfig config, CancellationToken cancellationToken)
    {
        var (ok, json, error) = await SendAsync(Request(HttpMethod.Get, "/v1/payment_links/" + Uri.EscapeDataString(attempt.GatewayReference), config), cancellationToken);
        if (!ok) return new(AttemptStatus.Processing, FailureReason.None, "status_unavailable", error);
        var status = (string?)json?["status"];
        var paymentId = json?["payments"] is JsonArray payments && payments.Count > 0 ? (string?)payments[^1]?["payment_id"] : null;
        return status switch
        {
            "paid" => new(AttemptStatus.Succeeded, FailureReason.None, "paid", "Paid on Razorpay") { ProviderPaymentId = paymentId, Method = (string?)(json?["payments"] as JsonArray)?.LastOrDefault()?["method"] },
            "expired" => new(AttemptStatus.Expired, FailureReason.Expired, "expired", "The Razorpay link expired"),
            "cancelled" => new(AttemptStatus.Cancelled, FailureReason.PayerCancelled, "cancelled", "The Razorpay link was cancelled"),
            _ => new(AttemptStatus.RequiresAction, FailureReason.None, status ?? "created", "Not paid yet"),
        };
    }

    public async Task<RefundResult> RefundAsync(RefundRequest request, ConnectorConfig config, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(request.Attempt.ProviderPaymentId))
            return new RefundResult(false, "", "Razorpay's payment id isn't known for this payment, so it can't be refunded from PayLink.") { Status = RefundState.Failed };
        var (ok, json, error) = await SendAsync(Request(HttpMethod.Post, $"/v1/payments/{Uri.EscapeDataString(request.Attempt.ProviderPaymentId)}/refund", config,
            new { amount = request.Amount, speed = "normal", receipt = request.RefundId, notes = new { reason = Truncate(request.Reason, 250) } }), cancellationToken);
        if (!ok) return new RefundResult(false, "", error) { Status = RefundState.Failed };
        return new RefundResult(true, (string?)json?["id"] ?? "", (string?)json?["status"] ?? "pending") { Status = State((string?)json?["status"]) };
    }

    public async Task<RefundResult> FetchRefundAsync(string refundId, string gatewayReference, ConnectorConfig config, CancellationToken cancellationToken)
    {
        var (ok, json, error) = await SendAsync(Request(HttpMethod.Get, "/v1/refunds/" + Uri.EscapeDataString(gatewayReference), config), cancellationToken);
        if (!ok) return new RefundResult(true, gatewayReference, error);
        return new RefundResult(true, gatewayReference, (string?)json?["status"] ?? "") { Status = State((string?)json?["status"]) };
    }

    private static RefundState State(string? status) => status switch
    {
        "processed" => RefundState.Succeeded,
        "failed" => RefundState.Failed,
        _ => RefundState.Pending,
    };

    public static string Hmac(string secret, string payload)
    {
        using var h = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        return Convert.ToHexString(h.ComputeHash(Encoding.UTF8.GetBytes(payload))).ToLowerInvariant();
    }

    private static bool FixedEquals(string a, string b) =>
        CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(a), Encoding.ASCII.GetBytes(b.Trim().ToLowerInvariant()));

    private static string Truncate(string? value, int max) => value is null ? "" : value.Length <= max ? value : value[..max];
}

internal static class StringExtensions
{
    public static string? NullIfEmpty(this string value) => value.Length == 0 ? null : value;
}
