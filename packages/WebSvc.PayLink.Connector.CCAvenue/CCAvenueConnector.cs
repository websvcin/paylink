using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using WebSvc.PayLink.Abstractions;

namespace WebSvc.PayLink.Connector.CCAvenue;

/// <summary>
/// CCAvenue's encrypted hosted checkout. The order is encrypted with the working key (AES-128-CBC, key = MD5(working key),
/// IV = 00..0F, hex) and posted to CCAvenue; CCAvenue posts the result back encrypted the same way (encResp), so only a
/// response made with the merchant's key can be read. Status and refunds use CCAvenue's API (orderStatusTracker, refundOrder).
/// </summary>
public sealed class CCAvenueConnector(HttpClient http) : IPaymentConnector, IPayerReturnHandler
{
    public const string Id = "ccavenue";
    public const string MerchantIdField = "merchant_id";
    public const string AccessCodeField = "access_code";
    public const string WorkingKeyField = "working_key";

    public static string TestCheckout { get; set; } = "https://test.ccavenue.com";
    public static string LiveCheckout { get; set; } = "https://secure.ccavenue.com";
    public static string TestApi { get; set; } = "https://apitest.ccavenue.com/apis/servlet/DoWebTrans";
    public static string LiveApi { get; set; } = "https://api.ccavenue.com/apis/servlet/DoWebTrans";

    private static readonly byte[] Iv = [0x00, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08, 0x09, 0x0a, 0x0b, 0x0c, 0x0d, 0x0e, 0x0f];

    public ConnectorManifest Manifest { get; } = new(
        Id, "CCAvenue", "0.1.0", AbstractionsMajorVersion: 0,
        Methods: [PaymentMethodKind.Upi, PaymentMethodKind.Card, PaymentMethodKind.NetBanking, PaymentMethodKind.Wallet, PaymentMethodKind.Emi],
        Currencies: ["INR", "USD", "EUR", "GBP", "AED", "SGD"],
        Capabilities: new ConnectorCapabilities(Refunds: true, PartialRefunds: true, Webhooks: false, StatusPolling: true),
        ConfigSchema:
        [
            new ConfigField(MerchantIdField, "Merchant ID", ConfigFieldType.Text, Required: true, Help: "CCAvenue Dashboard → Settings → API keys."),
            new ConfigField(AccessCodeField, "Access code", ConfigFieldType.Text, Required: true,
                Help: "Access codes are tied to a website: register PayLink's address in CCAvenue for the access code you enter here."),
            new ConfigField(WorkingKeyField, "Working key", ConfigFieldType.Secret, Required: true),
        ]);

    private static string V(ConnectorConfig c, string k) => c.Values.GetValueOrDefault(k)?.Trim() ?? "";

    public static string Encrypt(string plain, string workingKey)
    {
        using var aes = Aes.Create();
        aes.Key = MD5.HashData(Encoding.UTF8.GetBytes(workingKey));
        return Convert.ToHexString(aes.EncryptCbc(Encoding.UTF8.GetBytes(plain), Iv, PaddingMode.PKCS7)).ToLowerInvariant();
    }

    public static string Decrypt(string hex, string workingKey)
    {
        using var aes = Aes.Create();
        aes.Key = MD5.HashData(Encoding.UTF8.GetBytes(workingKey));
        return Encoding.UTF8.GetString(aes.DecryptCbc(Convert.FromHexString(hex.Trim()), Iv, PaddingMode.PKCS7));
    }

    private static Dictionary<string, string> ParseQuery(string query) =>
        query.Split('&', StringSplitOptions.RemoveEmptyEntries).Select(p => p.Split('=', 2))
            .GroupBy(p => Uri.UnescapeDataString(p[0])).ToDictionary(g => g.Key, g => Uri.UnescapeDataString(g.First().ElementAtOrDefault(1) ?? ""));

    private async Task<JsonNode?> ApiAsync(ConnectorConfig c, string command, object body, CancellationToken ct)
    {
        var form = new Dictionary<string, string>
        {
            ["enc_request"] = Encrypt(JsonSerializer.Serialize(body), V(c, WorkingKeyField)), ["access_code"] = V(c, AccessCodeField),
            ["command"] = command, ["request_type"] = "JSON", ["response_type"] = "JSON", ["version"] = "1.2",
        };
        using var response = await http.PostAsync(c.Mode == PaymentMode.Live ? LiveApi : TestApi, new FormUrlEncodedContent(form), ct);
        var parts = ParseQuery(await response.Content.ReadAsStringAsync(ct));
        if (parts.GetValueOrDefault("status") != "0")
            return new JsonObject { ["error"] = parts.GetValueOrDefault("enc_response") ?? parts.GetValueOrDefault("enc_error_code") ?? "CCAvenue refused the request." };
        try { return JsonNode.Parse(Decrypt(parts["enc_response"], V(c, WorkingKeyField))); }
        catch (Exception ex) when (ex is CryptographicException or FormatException or JsonException or KeyNotFoundException)
        { return new JsonObject { ["error"] = "CCAvenue's answer couldn't be decrypted with this working key." }; }
    }

    public async Task<ConnectionCheck> TestConnectionAsync(ConnectorConfig config, CancellationToken cancellationToken)
    {
        if (V(config, MerchantIdField).Length == 0 || V(config, AccessCodeField).Length == 0 || V(config, WorkingKeyField).Length == 0)
            return new(false, "Enter the merchant ID, access code and working key.");
        var json = await ApiAsync(config, "orderStatusTracker", new { order_no = "paylink_check" }, cancellationToken);
        return json?["error"] is { } e && !((string?)e ?? "").Contains("order", StringComparison.OrdinalIgnoreCase)
            ? new(false, "CCAvenue refused the access code or working key: " + e)
            : new(true, "Connected to CCAvenue.");
    }

    public Task<NextAction> CreateAttemptAsync(AttemptRequest request, ConnectorConfig config, CancellationToken cancellationToken)
    {
        if (V(config, WorkingKeyField).Length == 0)
            return Task.FromResult<NextAction>(new NextAction.Failed("", FailureReason.GatewayError, "not_configured", "CCAvenue isn't set up."));
        var data = new Dictionary<string, string>
        {
            ["merchant_id"] = V(config, MerchantIdField),
            ["order_id"] = request.AttemptId,
            ["currency"] = request.Currency.ToUpperInvariant(),
            ["amount"] = (request.Amount / 100m).ToString("0.00", CultureInfo.InvariantCulture),
            ["redirect_url"] = request.ReturnUrl,
            ["cancel_url"] = request.ReturnUrl,
            ["language"] = "EN",
            ["billing_name"] = request.Payer.Name ?? "",
            ["billing_email"] = request.Payer.Email ?? "",
            ["billing_tel"] = request.Payer.Phone ?? "",
            ["merchant_param1"] = request.PaymentId,
        };
        var plain = string.Join("&", data.Select(kv => $"{kv.Key}={Uri.EscapeDataString(kv.Value)}"));
        var url = (config.Mode == PaymentMode.Live ? LiveCheckout : TestCheckout) + "/transaction/transaction.do?command=initiateTransaction";
        return Task.FromResult<NextAction>(new NextAction.PostForm(request.AttemptId, url, new Dictionary<string, string>
        {
            ["encRequest"] = Encrypt(plain, V(config, WorkingKeyField)), ["access_code"] = V(config, AccessCodeField),
        }));
    }

    /// <summary>Reads CCAvenue's encResp. A response that doesn't decrypt with the merchant's working key is ignored.</summary>
    public GatewayEvent? ReadResponse(string encResp, ConnectorConfig config)
    {
        Dictionary<string, string> r;
        try { r = ParseQuery(Decrypt(encResp, V(config, WorkingKeyField))); }
        catch (Exception ex) when (ex is CryptographicException or FormatException) { return null; }
        var status = r.GetValueOrDefault("order_status", "");
        var (s, reason) = status switch
        {
            "Success" => (AttemptStatus.Succeeded, FailureReason.None),
            "Aborted" => (AttemptStatus.Cancelled, FailureReason.PayerCancelled),
            "Timeout" => (AttemptStatus.Failed, FailureReason.UpiTimeout),
            "Failure" or "Invalid" => (AttemptStatus.Failed, FailureReason.BankDeclined),
            _ => (AttemptStatus.Processing, FailureReason.None),
        };
        return new GatewayEvent(r.GetValueOrDefault("order_id", ""), r.GetValueOrDefault("order_id"), s, reason, status,
            r.GetValueOrDefault("failure_message") is { Length: > 0 } fm ? fm : r.GetValueOrDefault("status_message", status), "order_status=" + status)
        { ProviderPaymentId = r.GetValueOrDefault("tracking_id") };
    }

    public async Task<GatewayEvent?> ParseReturnAsync(HttpRequest request, AttemptReference attempt, ConnectorConfig config, CancellationToken cancellationToken)
    {
        if (!request.HasFormContentType) return null;
        var form = await request.ReadFormAsync(cancellationToken);
        var ev = ReadResponse(form["encResp"].ToString(), config);
        return ev is not null && ev.AttemptId == attempt.AttemptId ? ev : null;
    }

    public async Task<GatewayEvent> ParseWebhookAsync(HttpRequest request, ConnectorConfig config, CancellationToken cancellationToken)
    {
        if (!request.HasFormContentType) throw new WebhookVerificationException("CCAvenue notifications are form posts.");
        var form = await request.ReadFormAsync(cancellationToken);
        return ReadResponse(form["encResp"].ToString(), config) ?? throw new WebhookVerificationException("encResp doesn't decrypt with the working key.");
    }

    public async Task<AttemptStatusResult> FetchStatusAsync(AttemptReference attempt, ConnectorConfig config, CancellationToken cancellationToken)
    {
        var json = await ApiAsync(config, "orderStatusTracker", new { order_no = attempt.AttemptId }, cancellationToken);
        if (json?["error"] is { } e) return new(AttemptStatus.Processing, FailureReason.None, "status_unavailable", (string?)e ?? "");
        var status = (string?)json?["order_status"] ?? "";
        var tracking = json?["reference_no"]?.ToString();
        return status switch
        {
            "Shipped" or "Successful" or "Success" => new(AttemptStatus.Succeeded, FailureReason.None, status, "Paid on CCAvenue") { ProviderPaymentId = tracking },
            "Aborted" or "Auto-Cancelled" or "Cancelled" => new(AttemptStatus.Cancelled, FailureReason.PayerCancelled, status, status),
            "Unsuccessful" or "Invalid" or "Timeout" or "Fraud" or "Auto-Reversed" => new(AttemptStatus.Failed, FailureReason.BankDeclined, status, status),
            "Initiated" or "" => new(AttemptStatus.RequiresAction, FailureReason.None, status, "Not paid yet"),
            _ => new(AttemptStatus.Processing, FailureReason.None, status, status),
        };
    }

    public async Task<RefundResult> RefundAsync(RefundRequest request, ConnectorConfig config, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(request.Attempt.ProviderPaymentId))
            return new RefundResult(false, "", "CCAvenue's reference (tracking id) isn't known for this payment.") { Status = RefundState.Failed };
        var json = await ApiAsync(config, "refundOrder", new
        {
            reference_no = request.Attempt.ProviderPaymentId,
            refund_amount = (request.Amount / 100m).ToString("0.00", CultureInfo.InvariantCulture),
            refund_ref_no = request.RefundId,
        }, cancellationToken);
        var result = json?["Refund_Order_Result"];
        if (json?["error"] is { } e) return new RefundResult(false, "", (string?)e ?? "CCAvenue refused the refund.") { Status = RefundState.Failed };
        return result?["refund_status"]?.ToString() == "0"
            ? new RefundResult(true, request.RefundId, "Refunded") { Status = RefundState.Succeeded }
            : new RefundResult(false, "", (string?)result?["reason"] ?? "CCAvenue refused the refund.") { Status = RefundState.Failed };
    }
}
