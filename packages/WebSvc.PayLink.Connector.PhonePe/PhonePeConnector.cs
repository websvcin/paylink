using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using WebSvc.PayLink.Abstractions;

namespace WebSvc.PayLink.Connector.PhonePe;

/// <summary>
/// PhonePe Payment Gateway. "v2" is PhonePe's current Standard Checkout (OAuth client id/secret, O-Bearer token);
/// "v1" is the older salt-key API (X-VERIFY checksums) that many existing accounts still use. Either way the payer is
/// redirected to PhonePe's pay page and back; PayLink then checks the order's status (PhonePe's redirect carries no
/// signed result), and PhonePe's server-to-server callback confirms it too.
/// </summary>
public sealed class PhonePeConnector(HttpClient http) : IPaymentConnector, IRefundStatusFetcher
{
    public const string Id = "phonepe";
    public const string VersionField = "api_version";
    public const string ClientIdField = "client_id";
    public const string ClientSecretField = "client_secret";
    public const string ClientVersionField = "client_version";
    public const string WebhookUserField = "webhook_username";
    public const string WebhookPasswordField = "webhook_password";
    public const string MerchantIdField = "merchant_id";
    public const string SaltKeyField = "salt_key";
    public const string SaltIndexField = "salt_index";

    /// <summary>Hosts per mode; tests point these at a fake.</summary>
    public static string SandboxBase { get; set; } = "https://api-preprod.phonepe.com/apis/pg-sandbox";
    public static string LiveBase { get; set; } = "https://api.phonepe.com/apis/pg";
    public static string LiveAuthUrl { get; set; } = "https://api.phonepe.com/apis/identity-manager/v1/oauth/token";
    public static string LiveV1Base { get; set; } = "https://api.phonepe.com/apis/hermes";

    private static readonly ConcurrentDictionary<string, (string Token, DateTimeOffset Until)> Tokens = new();

    public ConnectorManifest Manifest { get; } = new(
        Id, "PhonePe", "0.1.0", AbstractionsMajorVersion: 0,
        Methods: [PaymentMethodKind.Upi, PaymentMethodKind.Card, PaymentMethodKind.NetBanking],
        Currencies: ["INR"],
        Capabilities: new ConnectorCapabilities(Refunds: true, PartialRefunds: true, Webhooks: true, StatusPolling: true),
        ConfigSchema:
        [
            new ConfigField(VersionField, "API", ConfigFieldType.Choice, Required: true,
                Help: "v2 (Standard Checkout, client id and secret) for accounts created since 2024; v1 (merchant id and salt key) for older ones.") { Options = ["v2", "v1"] },
            new ConfigField(ClientIdField, "Client ID (v2)", ConfigFieldType.Text, Required: false, Help: "PhonePe Business dashboard → Developer Settings."),
            new ConfigField(ClientSecretField, "Client secret (v2)", ConfigFieldType.Secret, Required: false),
            new ConfigField(ClientVersionField, "Client version (v2)", ConfigFieldType.Text, Required: false, Help: "Shown next to the client id, usually 1."),
            new ConfigField(WebhookUserField, "Webhook username (v2)", ConfigFieldType.Text, Required: false, Help: "The username and password you set for the webhook URL shown here in PhonePe's dashboard."),
            new ConfigField(WebhookPasswordField, "Webhook password (v2)", ConfigFieldType.Secret, Required: false),
            new ConfigField(MerchantIdField, "Merchant ID (v1)", ConfigFieldType.Text, Required: false),
            new ConfigField(SaltKeyField, "Salt key (v1)", ConfigFieldType.Secret, Required: false),
            new ConfigField(SaltIndexField, "Salt index (v1)", ConfigFieldType.Text, Required: false, Help: "Usually 1."),
        ]);

    private static string V(ConnectorConfig c, string key) => c.Values.GetValueOrDefault(key)?.Trim() ?? "";
    private static bool IsV1(ConnectorConfig c) => V(c, VersionField) == "v1";
    private static string Base(ConnectorConfig c) => c.Mode == PaymentMode.Live ? (IsV1(c) ? LiveV1Base : LiveBase) : SandboxBase;
    private static string AuthUrl(ConnectorConfig c) => c.Mode == PaymentMode.Live ? LiveAuthUrl : SandboxBase + "/v1/oauth/token";

    private static string? MissingSettings(ConnectorConfig c) => IsV1(c)
        ? (V(c, MerchantIdField).Length == 0 || V(c, SaltKeyField).Length == 0 ? "Enter the merchant ID and salt key (v1)." : null)
        : (V(c, ClientIdField).Length == 0 || V(c, ClientSecretField).Length == 0 ? "Enter the client ID and client secret (v2)." : null);

    // ---------- v2: OAuth ----------

    private async Task<string> TokenAsync(ConnectorConfig c, CancellationToken ct)
    {
        var cacheKey = $"{c.Mode}:{V(c, ClientIdField)}:{V(c, ClientSecretField).GetHashCode()}";
        if (Tokens.TryGetValue(cacheKey, out var cached) && cached.Until > DateTimeOffset.UtcNow.AddMinutes(1)) return cached.Token;
        using var response = await http.PostAsync(AuthUrl(c), new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = V(c, ClientIdField), ["client_secret"] = V(c, ClientSecretField),
            ["client_version"] = V(c, ClientVersionField) is { Length: > 0 } v ? v : "1", ["grant_type"] = "client_credentials",
        }), ct);
        var json = await ReadAsync(response, ct);
        if (!response.IsSuccessStatusCode || (string?)json?["access_token"] is not { } token)
            throw new PhonePeException("PhonePe refused the client id or secret: " + Message(json, response));
        var until = json!["expires_at"] is JsonValue e && e.TryGetValue<long>(out var at) ? DateTimeOffset.FromUnixTimeSeconds(at) : DateTimeOffset.UtcNow.AddMinutes(30);
        Tokens[cacheKey] = (token, until);
        return token;
    }

    private async Task<(bool Ok, JsonNode? Json, string Error)> V2Async(HttpMethod method, string path, ConnectorConfig c, object? body, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, Base(c) + path);
        request.Headers.Authorization = new AuthenticationHeaderValue("O-Bearer", await TokenAsync(c, ct));
        if (body is not null) request.Content = JsonContent.Create(body);
        using var response = await http.SendAsync(request, ct);
        var json = await ReadAsync(response, ct);
        return (response.IsSuccessStatusCode, json, response.IsSuccessStatusCode ? "" : Message(json, response));
    }

    // ---------- v1: X-VERIFY ----------

    public static string XVerify(string payload, string saltKey, string saltIndex) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload + saltKey))).ToLowerInvariant() + "###" + saltIndex;

    private async Task<(bool Ok, JsonNode? Json, string Error)> V1Async(HttpMethod method, string path, ConnectorConfig c, object? body, CancellationToken ct)
    {
        var salt = V(c, SaltKeyField);
        var index = V(c, SaltIndexField) is { Length: > 0 } i ? i : "1";
        using var request = new HttpRequestMessage(method, Base(c) + path);
        if (body is not null)
        {
            var b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(body)));
            request.Content = JsonContent.Create(new { request = b64 });
            request.Headers.Add("X-VERIFY", XVerify(b64 + path, salt, index));
        }
        else request.Headers.Add("X-VERIFY", XVerify(path, salt, index));
        request.Headers.Add("X-MERCHANT-ID", V(c, MerchantIdField));
        using var response = await http.SendAsync(request, ct);
        var json = await ReadAsync(response, ct);
        var ok = response.IsSuccessStatusCode && (bool?)json?["success"] != false;
        return (ok, json, ok ? "" : Message(json, response));
    }

    // ---------- the connector ----------

    public async Task<ConnectionCheck> TestConnectionAsync(ConnectorConfig config, CancellationToken cancellationToken)
    {
        if (MissingSettings(config) is { } missing) return new(false, missing);
        try
        {
            if (IsV1(config))
            {
                // A status check for an order that doesn't exist: wrong keys give KEY_NOT_CONFIGURED / UNAUTHORIZED, right keys "not found".
                var (_, json, _) = await V1Async(HttpMethod.Get, $"/pg/v1/status/{V(config, MerchantIdField)}/paylink_check", config, null, cancellationToken);
                var code = (string?)json?["code"] ?? "";
                return code is "KEY_NOT_CONFIGURED" or "UNAUTHORIZED" or "AUTHORIZATION_FAILED" or "BAD_REQUEST" && code != "PAYMENT_NOT_FOUND"
                    ? new(false, $"PhonePe refused the merchant id or salt key ({code}).")
                    : new(true, "Connected to PhonePe (v1).");
            }
            await TokenAsync(config, cancellationToken);
            return new(true, V(config, WebhookUserField).Length == 0
                ? "Connected to PhonePe. Add the webhook username and password too, so payments are confirmed even if the payer closes the browser."
                : "Connected to PhonePe.");
        }
        catch (PhonePeException ex) { return new(false, ex.Message); }
    }

    public async Task<NextAction> CreateAttemptAsync(AttemptRequest request, ConnectorConfig config, CancellationToken cancellationToken)
    {
        if (MissingSettings(config) is { } missing) return new NextAction.Failed("", FailureReason.GatewayError, "not_configured", missing);
        try
        {
            if (IsV1(config))
            {
                var (ok1, json1, error1) = await V1Async(HttpMethod.Post, "/pg/v1/pay", config, new
                {
                    merchantId = V(config, MerchantIdField), merchantTransactionId = request.AttemptId,
                    merchantUserId = "PL" + Hex(request.Payer.Email ?? request.PaymentId)[..16], amount = request.Amount,
                    redirectUrl = request.ReturnUrl, redirectMode = "REDIRECT", callbackUrl = config.WebhookUrl,
                    mobileNumber = Digits(request.Payer.Phone), paymentInstrument = new { type = "PAY_PAGE" },
                }, cancellationToken);
                var url1 = (string?)json1?["data"]?["instrumentResponse"]?["redirectInfo"]?["url"];
                return ok1 && url1 is not null
                    ? new NextAction.Redirect(request.AttemptId, url1)
                    : new NextAction.Failed("", FailureReason.GatewayError, (string?)json1?["code"] ?? "pay_failed", error1);
            }
            var (ok, json, error) = await V2Async(HttpMethod.Post, "/checkout/v2/pay", config, new
            {
                merchantOrderId = request.AttemptId, amount = request.Amount, expireAfter = 1200,
                metaInfo = new { udf1 = request.PaymentId },
                paymentFlow = new
                {
                    type = "PG_CHECKOUT", message = Truncate(request.Description, 100),
                    merchantUrls = new { redirectUrl = request.ReturnUrl },
                },
            }, cancellationToken);
            var url = (string?)json?["redirectUrl"];
            return ok && url is not null
                ? new NextAction.Redirect((string?)json!["orderId"] ?? request.AttemptId, url)
                : new NextAction.Failed("", FailureReason.GatewayError, (string?)json?["code"] ?? "pay_failed", error);
        }
        catch (PhonePeException ex) { return new NextAction.Failed("", FailureReason.GatewayError, "auth_failed", ex.Message); }
    }

    public async Task<GatewayEvent> ParseWebhookAsync(HttpRequest request, ConnectorConfig config, CancellationToken cancellationToken)
    {
        request.Body.Position = 0;
        var raw = await new StreamReader(request.Body).ReadToEndAsync(cancellationToken);
        if (IsV1(config))
        {
            var body = JsonNode.Parse(raw);
            var b64 = (string?)body?["response"] ?? throw new WebhookVerificationException("No response field.");
            var expected = XVerify(b64, V(config, SaltKeyField), V(config, SaltIndexField) is { Length: > 0 } i ? i : "1");
            if (!Same(expected, request.Headers["X-VERIFY"].ToString())) throw new WebhookVerificationException("X-VERIFY doesn't match.");
            var data = JsonNode.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(b64)));
            var code = (string?)data?["code"] ?? "";
            var tid = (string?)data?["data"]?["merchantTransactionId"];
            return new GatewayEvent(tid ?? "", tid, MapV1(code), code == "PAYMENT_SUCCESS" ? FailureReason.None : FailureReason.BankDeclined,
                code, (string?)data?["message"] ?? code, raw) { ProviderPaymentId = (string?)data?["data"]?["transactionId"] };
        }

        var user = V(config, WebhookUserField);
        var pass = V(config, WebhookPasswordField);
        if (user.Length == 0) throw new WebhookVerificationException("No webhook username is set for PhonePe.");
        var auth = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{user}:{pass}"))).ToLowerInvariant();
        var header = request.Headers.Authorization.ToString();
        if (header.StartsWith("SHA256 ", StringComparison.OrdinalIgnoreCase)) header = header[7..];
        if (!Same(auth, header)) throw new WebhookVerificationException("The Authorization header doesn't match the webhook username and password.");
        var json = JsonNode.Parse(raw)!;
        var ev = (string?)(json["event"] ?? json["type"]) ?? "";
        var payload = json["payload"];
        if (ev.Contains("refund", StringComparison.OrdinalIgnoreCase))
        {
            var state = (string?)payload?["state"] switch { "COMPLETED" => RefundState.Succeeded, "FAILED" => RefundState.Failed, _ => RefundState.Pending };
            return new GatewayEvent("", null, AttemptStatus.Succeeded, FailureReason.None, ev, (string?)payload?["state"] ?? ev, raw)
            { RefundId = (string?)payload?["merchantRefundId"], RefundGatewayReference = (string?)payload?["refundId"], RefundState = state };
        }
        var orderState = (string?)payload?["state"];
        var detail = (payload?["paymentDetails"] as JsonArray)?.LastOrDefault();
        return new GatewayEvent((string?)payload?["orderId"] ?? "", (string?)payload?["merchantOrderId"], MapV2(orderState),
            orderState == "FAILED" ? FailureReason.BankDeclined : FailureReason.None, (string?)detail?["errorCode"] ?? orderState ?? ev, ev, raw)
        { ProviderPaymentId = (string?)detail?["transactionId"] };
    }

    public async Task<AttemptStatusResult> FetchStatusAsync(AttemptReference attempt, ConnectorConfig config, CancellationToken cancellationToken)
    {
        try
        {
            if (IsV1(config))
            {
                var (_, json1, _) = await V1Async(HttpMethod.Get, $"/pg/v1/status/{V(config, MerchantIdField)}/{attempt.AttemptId}", config, null, cancellationToken);
                var code = (string?)json1?["code"] ?? "";
                return new(MapV1(code), code is "PAYMENT_ERROR" or "PAYMENT_DECLINED" ? FailureReason.BankDeclined : FailureReason.None, code, (string?)json1?["message"] ?? code)
                { ProviderPaymentId = (string?)json1?["data"]?["transactionId"] };
            }
            var (ok, json, error) = await V2Async(HttpMethod.Get, $"/checkout/v2/order/{Uri.EscapeDataString(attempt.AttemptId)}/status", config, null, cancellationToken);
            if (!ok) return new(AttemptStatus.Processing, FailureReason.None, "status_unavailable", error);
            var state = (string?)json?["state"];
            var detail = (json?["paymentDetails"] as JsonArray)?.LastOrDefault();
            return new(MapV2(state), state == "FAILED" ? FailureReason.BankDeclined : FailureReason.None,
                (string?)detail?["errorCode"] ?? state ?? "", (string?)detail?["detailedErrorCode"] ?? state ?? "")
            { ProviderPaymentId = (string?)detail?["transactionId"], Method = ((string?)detail?["paymentMode"])?.ToLowerInvariant() };
        }
        catch (PhonePeException ex) { return new(AttemptStatus.Processing, FailureReason.None, "auth_failed", ex.Message); }
    }

    public async Task<RefundResult> RefundAsync(RefundRequest request, ConnectorConfig config, CancellationToken cancellationToken)
    {
        try
        {
            if (IsV1(config))
            {
                var (ok1, json1, error1) = await V1Async(HttpMethod.Post, "/pg/v1/refund", config, new
                {
                    merchantId = V(config, MerchantIdField), merchantUserId = "PLREFUND", originalTransactionId = request.Attempt.AttemptId,
                    merchantTransactionId = request.RefundId, amount = request.Amount, callbackUrl = config.WebhookUrl,
                }, cancellationToken);
                return ok1
                    ? new RefundResult(true, (string?)json1?["data"]?["transactionId"] ?? request.RefundId, (string?)json1?["code"] ?? "") { Status = MapRefundV1((string?)json1?["code"]) }
                    : new RefundResult(false, "", error1) { Status = RefundState.Failed };
            }
            var (ok, json, error) = await V2Async(HttpMethod.Post, "/payments/v2/refund", config,
                new { merchantRefundId = request.RefundId, originalMerchantOrderId = request.Attempt.AttemptId, amount = request.Amount }, cancellationToken);
            return ok
                ? new RefundResult(true, (string?)json?["refundId"] ?? request.RefundId, (string?)json?["state"] ?? "PENDING") { Status = MapRefund((string?)json?["state"]) }
                : new RefundResult(false, "", error) { Status = RefundState.Failed };
        }
        catch (PhonePeException ex) { return new RefundResult(false, "", ex.Message) { Status = RefundState.Failed }; }
    }

    public async Task<RefundResult> FetchRefundAsync(string refundId, string gatewayReference, ConnectorConfig config, CancellationToken cancellationToken)
    {
        try
        {
            if (IsV1(config))
            {
                var (_, json1, _) = await V1Async(HttpMethod.Get, $"/pg/v1/status/{V(config, MerchantIdField)}/{refundId}", config, null, cancellationToken);
                return new RefundResult(true, gatewayReference, (string?)json1?["code"] ?? "") { Status = MapRefundV1((string?)json1?["code"]) };
            }
            var (ok, json, error) = await V2Async(HttpMethod.Get, $"/payments/v2/refund/{Uri.EscapeDataString(refundId)}/status", config, null, cancellationToken);
            return new RefundResult(true, gatewayReference, ok ? (string?)json?["state"] ?? "" : error) { Status = ok ? MapRefund((string?)json?["state"]) : RefundState.Pending };
        }
        catch (PhonePeException ex) { return new RefundResult(true, gatewayReference, ex.Message); }
    }

    private static AttemptStatus MapV2(string? state) => state switch
    {
        "COMPLETED" => AttemptStatus.Succeeded,
        "FAILED" => AttemptStatus.Failed,
        _ => AttemptStatus.Processing,
    };

    private static AttemptStatus MapV1(string code) => code switch
    {
        "PAYMENT_SUCCESS" => AttemptStatus.Succeeded,
        "PAYMENT_ERROR" or "PAYMENT_DECLINED" or "TIMED_OUT" or "AUTHORIZATION_FAILED" => AttemptStatus.Failed,
        _ => AttemptStatus.Processing,
    };

    private static RefundState MapRefund(string? state) => state switch { "COMPLETED" => RefundState.Succeeded, "FAILED" => RefundState.Failed, _ => RefundState.Pending };
    private static RefundState MapRefundV1(string? code) => code switch { "PAYMENT_SUCCESS" => RefundState.Succeeded, "PAYMENT_ERROR" or "PAYMENT_DECLINED" => RefundState.Failed, _ => RefundState.Pending };

    private static async Task<JsonNode?> ReadAsync(HttpResponseMessage r, CancellationToken ct)
    {
        var text = await r.Content.ReadAsStringAsync(ct);
        try { return string.IsNullOrWhiteSpace(text) ? null : JsonNode.Parse(text); } catch (JsonException) { return null; }
    }

    private static string Message(JsonNode? json, HttpResponseMessage r) =>
        (string?)json?["message"] ?? (string?)json?["code"] ?? $"PhonePe answered {(int)r.StatusCode}.";

    private static bool Same(string a, string b) =>
        CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(a.Trim().ToLowerInvariant()), Encoding.ASCII.GetBytes(b.Trim().ToLowerInvariant()));

    private static string Hex(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static string? Digits(string? phone) => phone is null ? null : new string(phone.Where(char.IsDigit).ToArray()) is { Length: >= 10 } d ? d[^10..] : null;
    private static string Truncate(string? value, int max) => value is null ? "" : value.Length <= max ? value : value[..max];
}

public sealed class PhonePeException(string message) : Exception(message);
