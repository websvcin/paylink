using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using WebSvc.PayLink.Abstractions;

namespace WebSvc.PayLink.Connector.PayPal;

/// <summary>
/// PayPal through Orders v2: PayLink creates an order for each attempt and sends the payer to approve it. An approved order still
/// has to be captured, so PayLink captures it when the payer comes back, or when it next checks the status if they closed the window.
/// Webhooks to <c>/hooks/{tenant}/paypal/{mode}</c> are verified by asking PayPal (verify-webhook-signature) with the webhook id.
/// Test settings use the sandbox; Live settings the live API.
/// </summary>
public sealed class PayPalConnector(HttpClient http) : IPaymentConnector, IPayerReturnHandler, IRefundStatusFetcher
{
    public const string Id = "paypal";
    public const string ClientIdField = "client_id";
    public const string ClientSecretField = "client_secret";
    public const string WebhookIdField = "webhook_id";
    public static string SandboxBase { get; set; } = "https://api-m.sandbox.paypal.com";
    public static string LiveBase { get; set; } = "https://api-m.paypal.com";

    public ConnectorManifest Manifest { get; } = new(
        Id, "PayPal", "0.1.0", AbstractionsMajorVersion: 0,
        Methods: [PaymentMethodKind.Wallet, PaymentMethodKind.Card, PaymentMethodKind.PayLater],
        Currencies: ["*"],
        Capabilities: new ConnectorCapabilities(Refunds: true, PartialRefunds: true, Webhooks: true, StatusPolling: true),
        ConfigSchema:
        [
            new ConfigField(ClientIdField, "Client ID", ConfigFieldType.Text, Required: true,
                Help: "PayPal Developer Dashboard → Apps & Credentials. Sandbox apps for Test, live apps for Live."),
            new ConfigField(ClientSecretField, "Secret", ConfigFieldType.Secret, Required: true),
            new ConfigField(WebhookIdField, "Webhook ID", ConfigFieldType.Text, Required: false,
                Help: "In the same app, add a webhook with the URL shown here and the events PAYMENT.CAPTURE.COMPLETED, PAYMENT.CAPTURE.DENIED, PAYMENT.CAPTURE.REFUNDED and CUSTOMER.DISPUTE.*, then paste its ID."),
        ]);

    /// <summary>Currencies PayPal takes without decimals; for them a PayLink amount is already whole units.</summary>
    private static readonly HashSet<string> NoDecimals = ["HUF", "JPY", "TWD"];

    private static readonly ConcurrentDictionary<string, (string Token, DateTimeOffset Until)> Tokens = new();

    private static string Base(ConnectorConfig c) => c.Mode == PaymentMode.Live ? LiveBase : SandboxBase;

    public static string Value(long amount, string currency) =>
        NoDecimals.Contains(currency.ToUpperInvariant()) ? amount.ToString(CultureInfo.InvariantCulture) : (amount / 100m).ToString("0.00", CultureInfo.InvariantCulture);

    public static long Minor(string? value, string currency) =>
        decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var v)
            ? NoDecimals.Contains(currency.ToUpperInvariant()) ? (long)v : (long)Math.Round(v * 100m, MidpointRounding.AwayFromZero)
            : 0;

    private async Task<(string? Token, string Error)> TokenAsync(ConnectorConfig c, CancellationToken ct)
    {
        var id = c.Values.GetValueOrDefault(ClientIdField) ?? "";
        var cacheKey = $"{Base(c)}|{id}|{c.Values.GetValueOrDefault(ClientSecretField)?.GetHashCode()}";
        if (Tokens.TryGetValue(cacheKey, out var cached) && cached.Until > DateTimeOffset.UtcNow) return (cached.Token, "");
        using var request = new HttpRequestMessage(HttpMethod.Post, Base(c) + "/v1/oauth2/token")
        {
            Content = new FormUrlEncodedContent([new("grant_type", "client_credentials")]),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{id}:{c.Values.GetValueOrDefault(ClientSecretField)}")));
        using var response = await http.SendAsync(request, ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        JsonNode? json = null;
        try { json = JsonNode.Parse(text); } catch (System.Text.Json.JsonException) { }
        if (!response.IsSuccessStatusCode || (string?)json?["access_token"] is not { } token)
            return (null, (string?)json?["error_description"] ?? $"PayPal answered {(int)response.StatusCode}.");
        var seconds = (int?)json!["expires_in"] ?? 3000;
        Tokens[cacheKey] = (token, DateTimeOffset.UtcNow.AddSeconds(Math.Max(60, seconds - 120)));
        return (token, "");
    }

    private async Task<(bool Ok, HttpStatusCode Status, JsonNode? Json, string Error)> SendAsync(HttpMethod method, string path, ConnectorConfig c, object? body,
        CancellationToken ct, string? requestId = null)
    {
        var (token, tokenError) = await TokenAsync(c, ct);
        if (token is null) return (false, HttpStatusCode.Unauthorized, null, "PayPal refused the client ID or secret: " + tokenError);
        using var request = new HttpRequestMessage(method, Base(c) + path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (requestId is not null) request.Headers.Add("PayPal-Request-Id", requestId);
        request.Headers.Add("Prefer", "return=representation");
        if (body is not null) request.Content = body is string raw ? new StringContent(raw, Encoding.UTF8, "application/json") : JsonContent.Create(body);
        using var response = await http.SendAsync(request, ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        JsonNode? json = null;
        try { json = string.IsNullOrWhiteSpace(text) ? null : JsonNode.Parse(text); } catch (System.Text.Json.JsonException) { }
        if (response.IsSuccessStatusCode) return (true, response.StatusCode, json, "");
        var detail = json?["details"] is JsonArray d && d.Count > 0 ? (string?)d[0]?["description"] ?? (string?)d[0]?["issue"] : null;
        return (false, response.StatusCode, json, detail ?? (string?)json?["message"] ?? $"PayPal answered {(int)response.StatusCode}.");
    }

    public async Task<ConnectionCheck> TestConnectionAsync(ConnectorConfig config, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(config.Values.GetValueOrDefault(ClientIdField)) || string.IsNullOrEmpty(config.Values.GetValueOrDefault(ClientSecretField)))
            return new(false, "Enter the client ID and secret.");
        var (token, error) = await TokenAsync(config, cancellationToken);
        if (token is null) return new(false, $"PayPal {(config.Mode == PaymentMode.Live ? "live" : "sandbox")} refused the client ID or secret: {error}");
        return new(true, string.IsNullOrEmpty(config.Values.GetValueOrDefault(WebhookIdField))
            ? "Connected. Add the webhook ID too, so refunds and disputes reach PayLink."
            : $"Connected to PayPal {(config.Mode == PaymentMode.Live ? "live" : "sandbox")}.");
    }

    public async Task<NextAction> CreateAttemptAsync(AttemptRequest request, ConnectorConfig config, CancellationToken cancellationToken)
    {
        var description = string.IsNullOrWhiteSpace(request.Description) ? request.PaymentId : request.Description;
        var body = new
        {
            intent = "CAPTURE",
            purchase_units = new[]
            {
                new
                {
                    reference_id = request.AttemptId,
                    custom_id = request.AttemptId,
                    invoice_id = request.AttemptId,
                    description = description.Length <= 127 ? description : description[..127],
                    amount = new { currency_code = request.Currency.ToUpperInvariant(), value = Value(request.Amount, request.Currency) },
                },
            },
            payment_source = new
            {
                paypal = new
                {
                    experience_context = new
                    {
                        return_url = request.ReturnUrl, cancel_url = request.ReturnUrl, user_action = "PAY_NOW", shipping_preference = "NO_SHIPPING",
                    },
                },
            },
        };
        var (ok, _, json, error) = await SendAsync(HttpMethod.Post, "/v2/checkout/orders", config, body, cancellationToken, requestId: request.AttemptId);
        var approve = (json?["links"] as JsonArray)?.FirstOrDefault(l => (string?)l?["rel"] is "payer-action" or "approve");
        if (!ok || (string?)approve?["href"] is not { } url)
            return new NextAction.Failed("", FailureReason.GatewayError, (string?)json?["name"] ?? "create_failed", error);
        return new NextAction.Redirect((string)json!["id"]!, url);
    }

    /// <summary>The payer is back (?token=ORDER_ID). PayLink doesn't trust the query string: it captures the order and reports what PayPal says.</summary>
    public async Task<GatewayEvent?> ParseReturnAsync(HttpRequest request, AttemptReference attempt, ConnectorConfig config, CancellationToken cancellationToken)
    {
        if (request.Query["token"].ToString() != attempt.GatewayReference) return null;
        var result = await FetchStatusAsync(attempt, config, cancellationToken);
        return new GatewayEvent(attempt.GatewayReference, attempt.AttemptId, result.Status, result.Reason, result.GatewayCode, result.GatewayMessage, request.QueryString.Value ?? "")
        {
            ProviderPaymentId = result.ProviderPaymentId,
        };
    }

    /// <summary>Reads the order, capturing it first if the payer approved it. Capturing twice is safe: PayPal-Request-Id makes it idempotent.</summary>
    public async Task<AttemptStatusResult> FetchStatusAsync(AttemptReference attempt, ConnectorConfig config, CancellationToken cancellationToken)
    {
        var order = Uri.EscapeDataString(attempt.GatewayReference);
        var (ok, _, json, error) = await SendAsync(HttpMethod.Get, $"/v2/checkout/orders/{order}", config, null, cancellationToken);
        if (!ok) return new(AttemptStatus.Processing, FailureReason.None, "status_unavailable", error);
        if ((string?)json?["status"] == "APPROVED")
        {
            var captured = await SendAsync(HttpMethod.Post, $"/v2/checkout/orders/{order}/capture", config, "{}", cancellationToken, requestId: "capture-" + attempt.AttemptId);
            if (captured.Ok) json = captured.Json;
            else if ((string?)(captured.Json?["details"] as JsonArray)?.FirstOrDefault()?["issue"] is "INSTRUMENT_DECLINED" or "TRANSACTION_REFUSED")
                return new(AttemptStatus.Failed, FailureReason.BankDeclined, "declined", captured.Error);
            else if (captured.Status != HttpStatusCode.UnprocessableEntity)
                return new(AttemptStatus.Processing, FailureReason.None, "capture_pending", captured.Error);
        }
        var capture = (json?["purchase_units"] as JsonArray)?.FirstOrDefault()?["payments"]?["captures"] is JsonArray caps ? caps.LastOrDefault() : null;
        return ((string?)json?["status"], (string?)capture?["status"]) switch
        {
            ("COMPLETED", "COMPLETED") => new(AttemptStatus.Succeeded, FailureReason.None, "completed", "Paid on PayPal") { ProviderPaymentId = (string?)capture!["id"], Method = "paypal" },
            ("COMPLETED", "DECLINED" or "FAILED") => new(AttemptStatus.Failed, FailureReason.BankDeclined, "declined", "PayPal declined the payment"),
            ("COMPLETED", _) => new(AttemptStatus.Processing, FailureReason.None, ((string?)capture?["status"] ?? "pending").ToLowerInvariant(), "PayPal is holding the payment") { ProviderPaymentId = (string?)capture?["id"] },
            ("VOIDED", _) => new(AttemptStatus.Cancelled, FailureReason.PayerCancelled, "voided", "The PayPal order was voided"),
            (var s, _) => new(AttemptStatus.RequiresAction, FailureReason.None, (s ?? "created").ToLowerInvariant(), "Not paid yet"),
        };
    }

    public async Task<GatewayEvent> ParseWebhookAsync(HttpRequest request, ConnectorConfig config, CancellationToken cancellationToken)
    {
        var webhookId = config.Values.GetValueOrDefault(WebhookIdField);
        if (string.IsNullOrEmpty(webhookId)) throw new WebhookVerificationException("No webhook ID is set for PayPal.");
        request.Body.Position = 0;
        var raw = await new StreamReader(request.Body).ReadToEndAsync(cancellationToken);
        string H(string name) => request.Headers[name].ToString();
        // PayPal checks its own signature (certificate chain included). The event goes back verbatim, so it's spliced in as raw JSON.
        var verifyBody = "{" +
            $"\"auth_algo\":{Q(H("PAYPAL-AUTH-ALGO"))},\"cert_url\":{Q(H("PAYPAL-CERT-URL"))},\"transmission_id\":{Q(H("PAYPAL-TRANSMISSION-ID"))}," +
            $"\"transmission_sig\":{Q(H("PAYPAL-TRANSMISSION-SIG"))},\"transmission_time\":{Q(H("PAYPAL-TRANSMISSION-TIME"))},\"webhook_id\":{Q(webhookId)}," +
            $"\"webhook_event\":{raw}}}";
        var (ok, _, verdict, error) = await SendAsync(HttpMethod.Post, "/v1/notifications/verify-webhook-signature", config, verifyBody, cancellationToken);
        if (!ok || (string?)verdict?["verification_status"] != "SUCCESS")
            throw new WebhookVerificationException(ok ? "PayPal says the signature doesn't verify." : "PayPal couldn't verify the notification: " + error);

        var json = JsonNode.Parse(raw)!;
        var type = (string?)json["event_type"] ?? "";
        var r = json["resource"] ?? new JsonObject();

        if (type.StartsWith("CUSTOMER.DISPUTE.", StringComparison.Ordinal))
        {
            var outcome = (string?)r["dispute_outcome"]?["outcome_code"];
            var state = (string?)r["status"] switch
            {
                "RESOLVED" => outcome == "RESOLVED_SELLER_FAVOUR" ? DisputeState.Won : outcome == "RESOLVED_BUYER_FAVOUR" ? DisputeState.Lost : DisputeState.Closed,
                "UNDER_REVIEW" => DisputeState.UnderReview,
                _ => DisputeState.NeedsResponse,
            };
            var amount = r["dispute_amount"];
            var currency = (string?)amount?["currency_code"] ?? "USD";
            return new GatewayEvent("", null, AttemptStatus.Succeeded, FailureReason.None, type, (string?)r["reason"] ?? type, raw)
            {
                ProviderPaymentId = (string?)(r["disputed_transactions"] as JsonArray)?.FirstOrDefault()?["seller_transaction_id"],
                Dispute = new DisputeInfo((string?)r["dispute_id"] ?? "", state, Minor((string?)amount?["value"], currency), currency, (string?)r["reason"])
                {
                    RespondBy = DateTimeOffset.TryParse((string?)r["seller_response_due_date"], CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var due) ? due : null,
                },
            };
        }
        if (type is "PAYMENT.CAPTURE.REFUNDED" or "PAYMENT.REFUND.COMPLETED" or "PAYMENT.REFUND.FAILED" or "PAYMENT.REFUND.PENDING")
            return new GatewayEvent("", null, AttemptStatus.Succeeded, FailureReason.None, type, (string?)r["status"] ?? type, raw)
            {
                RefundGatewayReference = (string?)r["id"], RefundId = (string?)r["invoice_id"], RefundState = State((string?)r["status"]),
            };

        var orderId = (string?)r["supplementary_data"]?["related_ids"]?["order_id"] ?? "";
        var attemptId = (string?)r["custom_id"];
        return type switch
        {
            "PAYMENT.CAPTURE.COMPLETED" => new GatewayEvent(orderId, attemptId, AttemptStatus.Succeeded, FailureReason.None, type, "Paid on PayPal", raw) { ProviderPaymentId = (string?)r["id"] },
            "PAYMENT.CAPTURE.DENIED" or "PAYMENT.CAPTURE.DECLINED" => new GatewayEvent(orderId, attemptId, AttemptStatus.Failed, FailureReason.BankDeclined, type, "PayPal declined the payment", raw),
            "PAYMENT.CAPTURE.PENDING" => new GatewayEvent(orderId, attemptId, AttemptStatus.Processing, FailureReason.None, type, "PayPal is holding the payment", raw) { ProviderPaymentId = (string?)r["id"] },
            // The payer approved but PayLink hasn't captured yet: report Processing; the capture happens on return or at the next status check.
            _ => new GatewayEvent((string?)r["id"] ?? orderId, attemptId ?? (string?)(r["purchase_units"] as JsonArray)?.FirstOrDefault()?["custom_id"], AttemptStatus.Processing, FailureReason.None, type, type, raw),
        };
    }

    public async Task<RefundResult> RefundAsync(RefundRequest request, ConnectorConfig config, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(request.Attempt.ProviderPaymentId))
            return new RefundResult(false, "", "PayPal's capture id isn't known for this payment, so it can't be refunded from PayLink.") { Status = RefundState.Failed };
        var note = string.IsNullOrWhiteSpace(request.Reason) ? null : request.Reason.Length <= 255 ? request.Reason : request.Reason[..255];
        var (ok, _, json, error) = await SendAsync(HttpMethod.Post, $"/v2/payments/captures/{Uri.EscapeDataString(request.Attempt.ProviderPaymentId)}/refund", config,
            new { amount = new { value = Value(request.Amount, request.Currency), currency_code = request.Currency.ToUpperInvariant() }, invoice_id = request.RefundId, note_to_payer = note },
            cancellationToken, requestId: request.RefundId);
        if (!ok) return new RefundResult(false, "", error) { Status = RefundState.Failed };
        return new RefundResult(true, (string?)json?["id"] ?? "", (string?)json?["status"] ?? "PENDING") { Status = State((string?)json?["status"]) };
    }

    public async Task<RefundResult> FetchRefundAsync(string refundId, string gatewayReference, ConnectorConfig config, CancellationToken cancellationToken)
    {
        var (ok, _, json, error) = await SendAsync(HttpMethod.Get, "/v2/payments/refunds/" + Uri.EscapeDataString(gatewayReference), config, null, cancellationToken);
        if (!ok) return new RefundResult(true, gatewayReference, error);
        return new RefundResult(true, gatewayReference, (string?)json?["status"] ?? "") { Status = State((string?)json?["status"]) };
    }

    private static RefundState State(string? status) => status switch
    {
        "COMPLETED" => RefundState.Succeeded,
        "FAILED" or "CANCELLED" => RefundState.Failed,
        _ => RefundState.Pending,
    };

    private static string Q(string value) => System.Text.Json.JsonSerializer.Serialize(value);
}
