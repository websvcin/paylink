using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using WebSvc.PayLink.Abstractions;

namespace WebSvc.PayLink.Connector.Stripe;

/// <summary>
/// Stripe through Checkout Sessions: PayLink creates a session for each attempt and sends the payer to Stripe's page; Stripe returns
/// the payer to PayLink (which then asks Stripe, since return URLs aren't signed) and notifies <c>/hooks/{tenant}/stripe/{mode}</c>
/// (Stripe-Signature). The key decides the mode (sk_test_… / sk_live_…; restricted rk_ keys work too).
/// </summary>
public sealed class StripeConnector(HttpClient http) : IPaymentConnector, IRefundStatusFetcher
{
    public const string Id = "stripe";
    public const string SecretKeyField = "secret_key";
    public const string WebhookSecretField = "webhook_secret";
    public static string ApiBase { get; set; } = "https://api.stripe.com";
    /// <summary>Webhooks older than this are refused, so a captured one can't be replayed later.</summary>
    public static readonly TimeSpan SignatureTolerance = TimeSpan.FromMinutes(5);

    public ConnectorManifest Manifest { get; } = new(
        Id, "Stripe", "0.1.0", AbstractionsMajorVersion: 0,
        Methods: [PaymentMethodKind.Card, PaymentMethodKind.Wallet, PaymentMethodKind.Other],
        Currencies: ["*"],
        Capabilities: new ConnectorCapabilities(Refunds: true, PartialRefunds: true, Webhooks: true, StatusPolling: true),
        ConfigSchema:
        [
            new ConfigField(SecretKeyField, "Secret key", ConfigFieldType.Secret, Required: true,
                Help: "Stripe Dashboard → Developers → API keys. Test keys start with sk_test_, live keys with sk_live_ (or rk_ for a restricted key)."),
            new ConfigField(WebhookSecretField, "Webhook signing secret", ConfigFieldType.Secret, Required: false,
                Help: "Stripe Dashboard → Developers → Webhooks: add the webhook URL shown here with checkout.session.completed, checkout.session.async_payment_succeeded, checkout.session.async_payment_failed, checkout.session.expired, refund.updated and charge.dispute.* events, then paste its whsec_ secret."),
        ]);

    private HttpRequestMessage Request(HttpMethod method, string path, ConnectorConfig config, IEnumerable<KeyValuePair<string, string>>? form = null, string? idempotencyKey = null)
    {
        var r = new HttpRequestMessage(method, ApiBase + path);
        r.Headers.Authorization = new AuthenticationHeaderValue("Bearer", config.Values.GetValueOrDefault(SecretKeyField) ?? "");
        if (idempotencyKey is not null) r.Headers.Add("Idempotency-Key", idempotencyKey);
        if (form is not null) r.Content = new FormUrlEncodedContent(form);
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
            return (false, json, (string?)json?["error"]?["message"] ?? $"Stripe answered {(int)response.StatusCode}.");
        }
    }

    public async Task<ConnectionCheck> TestConnectionAsync(ConnectorConfig config, CancellationToken cancellationToken)
    {
        var key = config.Values.GetValueOrDefault(SecretKeyField) ?? "";
        var mode = config.Mode == PaymentMode.Live ? "live" : "test";
        if (!key.StartsWith($"sk_{mode}_", StringComparison.Ordinal) && !key.StartsWith($"rk_{mode}_", StringComparison.Ordinal))
            return new(false, $"{(config.Mode == PaymentMode.Live ? "Live" : "Test")} settings need a key starting with sk_{mode}_ (or rk_{mode}_).");
        var (ok, _, error) = await SendAsync(Request(HttpMethod.Get, "/v1/balance", config), cancellationToken);
        if (!ok) return new(false, "Stripe refused the key: " + error);
        return new(true, string.IsNullOrEmpty(config.Values.GetValueOrDefault(WebhookSecretField))
            ? "Connected. Add the webhook signing secret too, so payments are confirmed even if the payer closes the browser."
            : "Connected to Stripe.");
    }

    public async Task<NextAction> CreateAttemptAsync(AttemptRequest request, ConnectorConfig config, CancellationToken cancellationToken)
    {
        var name = string.IsNullOrWhiteSpace(request.Description) ? request.PaymentId : request.Description;
        var form = new List<KeyValuePair<string, string>>
        {
            new("mode", "payment"),
            new("client_reference_id", request.AttemptId),
            new("line_items[0][quantity]", "1"),
            new("line_items[0][price_data][currency]", request.Currency.ToLowerInvariant()),
            new("line_items[0][price_data][unit_amount]", request.Amount.ToString(CultureInfo.InvariantCulture)),
            new("line_items[0][price_data][product_data][name]", name.Length <= 250 ? name : name[..250]),
            new("success_url", request.ReturnUrl),
            new("cancel_url", request.ReturnUrl),
            new("metadata[paylink_payment]", request.PaymentId),
            new("metadata[paylink_attempt]", request.AttemptId),
            new("payment_intent_data[metadata][paylink_payment]", request.PaymentId),
            new("payment_intent_data[metadata][paylink_attempt]", request.AttemptId),
        };
        if (request.Payer.Email is { Length: > 0 } email) form.Add(new("customer_email", email));
        var (ok, json, error) = await SendAsync(Request(HttpMethod.Post, "/v1/checkout/sessions", config, form, idempotencyKey: request.AttemptId), cancellationToken);
        if (!ok || (string?)json?["url"] is not { } url)
            return new NextAction.Failed("", FailureReason.GatewayError, (string?)json?["error"]?["code"] ?? "create_failed", error);
        return new NextAction.Redirect((string)json!["id"]!, url);
    }

    public async Task<AttemptStatusResult> FetchStatusAsync(AttemptReference attempt, ConnectorConfig config, CancellationToken cancellationToken)
    {
        var (ok, json, error) = await SendAsync(Request(HttpMethod.Get, $"/v1/checkout/sessions/{Uri.EscapeDataString(attempt.GatewayReference)}?expand[]=payment_intent", config), cancellationToken);
        if (!ok) return new(AttemptStatus.Processing, FailureReason.None, "status_unavailable", error);
        return FromSession(json!);
    }

    private static AttemptStatusResult FromSession(JsonNode session)
    {
        var status = (string?)session["status"];
        var paid = (string?)session["payment_status"];
        var intent = session["payment_intent"];
        var intentId = intent is JsonObject o ? (string?)o["id"] : (string?)intent;
        var method = intent is JsonObject io && io["payment_method_types"] is JsonArray types && types.Count > 0 ? (string?)types[0] : null;
        return (status, paid) switch
        {
            ("complete", "paid" or "no_payment_required") => new(AttemptStatus.Succeeded, FailureReason.None, "paid", "Paid on Stripe") { ProviderPaymentId = intentId, Method = method },
            ("complete", _) => new(AttemptStatus.Processing, FailureReason.None, paid ?? "unpaid", "Stripe is waiting for the payment to clear") { ProviderPaymentId = intentId },
            ("expired", _) => new(AttemptStatus.Expired, FailureReason.Expired, "expired", "The Stripe checkout expired"),
            _ => new(AttemptStatus.RequiresAction, FailureReason.None, status ?? "open", "Not paid yet"),
        };
    }

    public async Task<GatewayEvent> ParseWebhookAsync(HttpRequest request, ConnectorConfig config, CancellationToken cancellationToken)
    {
        var secret = config.Values.GetValueOrDefault(WebhookSecretField);
        if (string.IsNullOrEmpty(secret)) throw new WebhookVerificationException("No webhook signing secret is set for Stripe.");
        request.Body.Position = 0;
        var raw = await new StreamReader(request.Body).ReadToEndAsync(cancellationToken);
        Verify(secret, raw, request.Headers["Stripe-Signature"].ToString(), DateTimeOffset.UtcNow);

        var json = JsonNode.Parse(raw)!;
        var type = (string?)json["type"] ?? "";
        var obj = json["data"]?["object"] ?? new JsonObject();

        if (type.StartsWith("charge.dispute.", StringComparison.Ordinal))
        {
            var state = (string?)obj["status"] switch
            {
                "won" => DisputeState.Won,
                "lost" => DisputeState.Lost,
                "under_review" or "warning_under_review" => DisputeState.UnderReview,
                "warning_closed" => DisputeState.Closed,
                _ => DisputeState.NeedsResponse,
            };
            var due = (long?)obj["evidence_details"]?["due_by"];
            return new GatewayEvent("", null, AttemptStatus.Succeeded, FailureReason.None, type, (string?)obj["reason"] ?? type, raw)
            {
                ProviderPaymentId = (string?)obj["payment_intent"],
                Dispute = new DisputeInfo((string?)obj["id"] ?? "", state, (long?)obj["amount"] ?? 0, ((string?)obj["currency"] ?? "").ToUpperInvariant(), (string?)obj["reason"])
                {
                    RespondBy = due is { } d ? DateTimeOffset.FromUnixTimeSeconds(d) : null,
                },
            };
        }
        if (type.StartsWith("refund.", StringComparison.Ordinal) || type == "charge.refund.updated")
            return new GatewayEvent("", null, AttemptStatus.Succeeded, FailureReason.None, type, (string?)obj["status"] ?? type, raw)
            {
                RefundGatewayReference = (string?)obj["id"], RefundId = (string?)obj["metadata"]?["paylink_refund"], RefundState = State((string?)obj["status"]),
                ProviderPaymentId = (string?)obj["payment_intent"],
            };

        var sessionId = (string?)obj["id"] ?? "";
        var attemptId = (string?)obj["client_reference_id"] ?? (string?)obj["metadata"]?["paylink_attempt"];
        var intentId = (string?)obj["payment_intent"];
        var (status, reason) = type switch
        {
            "checkout.session.completed" => FromSession(obj).Status == AttemptStatus.Succeeded ? (AttemptStatus.Succeeded, FailureReason.None) : (AttemptStatus.Processing, FailureReason.None),
            "checkout.session.async_payment_succeeded" => (AttemptStatus.Succeeded, FailureReason.None),
            "checkout.session.async_payment_failed" => (AttemptStatus.Failed, FailureReason.BankDeclined),
            "checkout.session.expired" => (AttemptStatus.Expired, FailureReason.Expired),
            _ => (AttemptStatus.Processing, FailureReason.None),
        };
        return new GatewayEvent(sessionId, attemptId, status, reason, type, type, raw) { ProviderPaymentId = intentId };
    }

    /// <summary>Stripe-Signature: t=timestamp,v1=hex HMAC-SHA256(secret, "{t}.{raw body}"); any v1 may match (secrets roll over).</summary>
    public static void Verify(string secret, string raw, string header, DateTimeOffset now)
    {
        var parts = header.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(p => p.Split('=', 2)).Where(p => p.Length == 2).ToList();
        var t = parts.FirstOrDefault(p => p[0] == "t")?[1];
        if (t is null || !long.TryParse(t, out var seconds)) throw new WebhookVerificationException("Stripe-Signature has no timestamp.");
        if ((now - DateTimeOffset.FromUnixTimeSeconds(seconds)).Duration() > SignatureTolerance)
            throw new WebhookVerificationException("Stripe-Signature is too old (or the clocks differ by more than 5 minutes).");
        var expected = Encoding.ASCII.GetBytes(Sign(secret, t, raw));
        if (!parts.Where(p => p[0] == "v1").Any(p => CryptographicOperations.FixedTimeEquals(expected, Encoding.ASCII.GetBytes(p[1].ToLowerInvariant()))))
            throw new WebhookVerificationException("Stripe-Signature doesn't match.");
    }

    public static string Sign(string secret, string timestamp, string raw)
    {
        using var h = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        return Convert.ToHexString(h.ComputeHash(Encoding.UTF8.GetBytes($"{timestamp}.{raw}"))).ToLowerInvariant();
    }

    public async Task<RefundResult> RefundAsync(RefundRequest request, ConnectorConfig config, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(request.Attempt.ProviderPaymentId))
            return new RefundResult(false, "", "Stripe's payment id isn't known for this payment, so it can't be refunded from PayLink.") { Status = RefundState.Failed };
        var form = new List<KeyValuePair<string, string>>
        {
            new("payment_intent", request.Attempt.ProviderPaymentId),
            new("amount", request.Amount.ToString(CultureInfo.InvariantCulture)),
            new("reason", "requested_by_customer"),
            new("metadata[paylink_refund]", request.RefundId),
        };
        if (!string.IsNullOrWhiteSpace(request.Reason)) form.Add(new("metadata[reason]", request.Reason.Length <= 500 ? request.Reason : request.Reason[..500]));
        var (ok, json, error) = await SendAsync(Request(HttpMethod.Post, "/v1/refunds", config, form, idempotencyKey: request.RefundId), cancellationToken);
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
        "succeeded" => RefundState.Succeeded,
        "failed" or "canceled" => RefundState.Failed,
        _ => RefundState.Pending,
    };
}
