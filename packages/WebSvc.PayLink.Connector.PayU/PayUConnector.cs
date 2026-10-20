using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using WebSvc.PayLink.Abstractions;

namespace WebSvc.PayLink.Connector.PayU;

/// <summary>
/// PayU India hosted checkout. The payer's browser posts a hashed form to PayU (_payment); PayU posts the result back to
/// PayLink (surl/furl) with a reverse hash, which is checked before anything changes. Status checks and refunds use PayU's
/// merchant postservice API (verify_payment, cancel_refund_transaction). The same hashed form also arrives as PayU's
/// server-to-server webhook.
/// </summary>
public sealed class PayUConnector(HttpClient http) : IPaymentConnector, IPayerReturnHandler
{
    public const string Id = "payu";
    public const string KeyField = "merchant_key";
    public const string SaltField = "merchant_salt";

    public static string TestCheckout { get; set; } = "https://test.payu.in";
    public static string LiveCheckout { get; set; } = "https://secure.payu.in";
    public static string TestApi { get; set; } = "https://test.payu.in/merchant/postservice.php?form=2";
    public static string LiveApi { get; set; } = "https://info.payu.in/merchant/postservice.php?form=2";

    public ConnectorManifest Manifest { get; } = new(
        Id, "PayU", "0.1.0", AbstractionsMajorVersion: 0,
        Methods: [PaymentMethodKind.Upi, PaymentMethodKind.Card, PaymentMethodKind.NetBanking, PaymentMethodKind.Wallet, PaymentMethodKind.Emi],
        Currencies: ["INR"],
        Capabilities: new ConnectorCapabilities(Refunds: true, PartialRefunds: true, Webhooks: true, StatusPolling: true),
        ConfigSchema:
        [
            new ConfigField(KeyField, "Merchant key", ConfigFieldType.Text, Required: true, Help: "PayU Dashboard → Developers → API keys (test and live keys differ)."),
            new ConfigField(SaltField, "Merchant salt (v1)", ConfigFieldType.Secret, Required: true,
                Help: "For webhooks, add the webhook URL shown here in PayU Dashboard → Webhooks for successful, failed and refund events."),
        ]);

    private static string K(ConnectorConfig c) => c.Values.GetValueOrDefault(KeyField)?.Trim() ?? "";
    private static string S(ConnectorConfig c) => c.Values.GetValueOrDefault(SaltField)?.Trim() ?? "";

    public static string Sha512(string value) => Convert.ToHexString(SHA512.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    public static string Amount(long minor) => (minor / 100m).ToString("0.00", CultureInfo.InvariantCulture);

    /// <summary>sha512(key|txnid|amount|productinfo|firstname|email|udf1|udf2|udf3|udf4|udf5||||||SALT)</summary>
    public static string RequestHash(IReadOnlyDictionary<string, string> f, string salt) =>
        Sha512(string.Join("|", f["key"], f["txnid"], f["amount"], f["productinfo"], f["firstname"], f["email"],
            f.GetValueOrDefault("udf1", ""), f.GetValueOrDefault("udf2", ""), f.GetValueOrDefault("udf3", ""), f.GetValueOrDefault("udf4", ""), f.GetValueOrDefault("udf5", ""),
            "", "", "", "", "", salt));

    /// <summary>
    /// sha512([additionalCharges|]SALT|status||||||udf5|udf4|udf3|udf2|udf1|email|firstname|productinfo|amount|txnid|key)
    /// </summary>
    public static string ResponseHash(IReadOnlyDictionary<string, string> f, string salt)
    {
        string F(string k) => f.GetValueOrDefault(k, "");
        var core = string.Join("|", salt, F("status"), "", "", "", "", "", F("udf5"), F("udf4"), F("udf3"), F("udf2"), F("udf1"),
            F("email"), F("firstname"), F("productinfo"), F("amount"), F("txnid"), F("key"));
        return Sha512(F("additionalCharges") is { Length: > 0 } extra ? extra + "|" + core : core);
    }

    private async Task<JsonNode?> ApiAsync(ConnectorConfig c, string command, string var1, CancellationToken ct, params (string, string)[] more)
    {
        var form = new Dictionary<string, string>
        {
            ["key"] = K(c), ["command"] = command, ["var1"] = var1, ["hash"] = Sha512($"{K(c)}|{command}|{var1}|{S(c)}"),
        };
        foreach (var (k, v) in more) form[k] = v;
        using var response = await http.PostAsync(c.Mode == PaymentMode.Live ? LiveApi : TestApi, new FormUrlEncodedContent(form), ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        try { return JsonNode.Parse(text); } catch (System.Text.Json.JsonException) { return new JsonObject { ["status"] = 0, ["msg"] = text.Length > 200 ? text[..200] : text }; }
    }

    public async Task<ConnectionCheck> TestConnectionAsync(ConnectorConfig config, CancellationToken cancellationToken)
    {
        if (K(config).Length == 0 || S(config).Length == 0) return new(false, "Enter the merchant key and salt.");
        var json = await ApiAsync(config, "verify_payment", "paylink_connection_check", cancellationToken);
        var msg = (string?)json?["msg"] ?? "";
        return msg.Contains("invalid", StringComparison.OrdinalIgnoreCase) || msg.Contains("hash", StringComparison.OrdinalIgnoreCase)
            ? new(false, "PayU refused the key or salt: " + msg)
            : new(true, "Connected to PayU.");
    }

    public Task<NextAction> CreateAttemptAsync(AttemptRequest request, ConnectorConfig config, CancellationToken cancellationToken)
    {
        if (K(config).Length == 0 || S(config).Length == 0)
            return Task.FromResult<NextAction>(new NextAction.Failed("", FailureReason.GatewayError, "not_configured", "PayU isn't set up."));
        var fields = new Dictionary<string, string>
        {
            ["key"] = K(config),
            ["txnid"] = request.AttemptId,
            ["amount"] = Amount(request.Amount),
            ["productinfo"] = Clean(string.IsNullOrWhiteSpace(request.Description) ? request.PaymentId : request.Description, 100),
            ["firstname"] = Clean(request.Payer.Name ?? "Customer", 60),
            ["email"] = request.Payer.Email ?? "",
            ["phone"] = request.Payer.Phone ?? "",
            ["surl"] = request.ReturnUrl,
            ["furl"] = request.ReturnUrl,
            ["udf1"] = request.PaymentId,
        };
        fields["hash"] = RequestHash(fields, S(config));
        return Task.FromResult<NextAction>(new NextAction.PostForm(request.AttemptId, (config.Mode == PaymentMode.Live ? LiveCheckout : TestCheckout) + "/_payment", fields));
    }

    private GatewayEvent? Read(IReadOnlyDictionary<string, string> f, ConnectorConfig config, string raw)
    {
        if (!f.TryGetValue("hash", out var hash) || f.GetValueOrDefault("key") != K(config)) return null;
        if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(ResponseHash(f, S(config))), Encoding.ASCII.GetBytes(hash.ToLowerInvariant()))) return null;
        var status = f.GetValueOrDefault("status", "");
        var (s, reason) = status switch
        {
            "success" => (AttemptStatus.Succeeded, FailureReason.None),
            "failure" => (f.GetValueOrDefault("unmappedstatus") == "userCancelled" ? AttemptStatus.Cancelled : AttemptStatus.Failed,
                f.GetValueOrDefault("unmappedstatus") == "userCancelled" ? FailureReason.PayerCancelled : FailureReason.BankDeclined),
            _ => (AttemptStatus.Processing, FailureReason.None),
        };
        return new GatewayEvent(f.GetValueOrDefault("txnid", ""), f.GetValueOrDefault("txnid"), s, reason,
            f.GetValueOrDefault("error", status), f.GetValueOrDefault("error_Message", f.GetValueOrDefault("field9", status)), raw)
        { ProviderPaymentId = f.GetValueOrDefault("mihpayid") };
    }

    public async Task<GatewayEvent?> ParseReturnAsync(HttpRequest request, AttemptReference attempt, ConnectorConfig config, CancellationToken cancellationToken)
    {
        if (!request.HasFormContentType) return null;
        var form = await request.ReadFormAsync(cancellationToken);
        var f = form.ToDictionary(kv => kv.Key, kv => kv.Value.ToString());
        var ev = Read(f, config, string.Join("&", f.Where(kv => kv.Key != "hash").Select(kv => $"{kv.Key}={kv.Value}")));
        return ev is not null && ev.AttemptId == attempt.AttemptId ? ev : null;
    }

    public async Task<GatewayEvent> ParseWebhookAsync(HttpRequest request, ConnectorConfig config, CancellationToken cancellationToken)
    {
        if (!request.HasFormContentType) throw new WebhookVerificationException("PayU webhooks are form posts.");
        var form = await request.ReadFormAsync(cancellationToken);
        var f = form.ToDictionary(kv => kv.Key, kv => kv.Value.ToString());
        return Read(f, config, string.Join("&", f.Where(kv => kv.Key != "hash").Select(kv => $"{kv.Key}={kv.Value}")))
            ?? throw new WebhookVerificationException("The PayU hash doesn't match.");
    }

    public async Task<AttemptStatusResult> FetchStatusAsync(AttemptReference attempt, ConnectorConfig config, CancellationToken cancellationToken)
    {
        var json = await ApiAsync(config, "verify_payment", attempt.AttemptId, cancellationToken);
        var detail = json?["transaction_details"]?[attempt.AttemptId];
        var status = (string?)detail?["status"];
        return status switch
        {
            "success" => new(AttemptStatus.Succeeded, FailureReason.None, "success", "Paid on PayU") { ProviderPaymentId = (string?)detail?["mihpayid"], Method = ((string?)detail?["mode"])?.ToLowerInvariant() },
            "failure" => new(AttemptStatus.Failed, FailureReason.BankDeclined, (string?)detail?["error_code"] ?? "failure", (string?)detail?["error_Message"] ?? "Failed on PayU"),
            "Not Found" or null => new(AttemptStatus.RequiresAction, FailureReason.None, "not_found", "Not paid yet"),
            _ => new(AttemptStatus.Processing, FailureReason.None, status, "Pending at PayU"),
        };
    }

    public async Task<RefundResult> RefundAsync(RefundRequest request, ConnectorConfig config, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(request.Attempt.ProviderPaymentId))
            return new RefundResult(false, "", "PayU's payment id (mihpayid) isn't known for this payment.") { Status = RefundState.Failed };
        var json = await ApiAsync(config, "cancel_refund_transaction", request.Attempt.ProviderPaymentId, cancellationToken,
            ("var2", request.RefundId), ("var3", Amount(request.Amount)));
        var ok = (int?)json?["status"] == 1;
        return ok
            ? new RefundResult(true, json?["request_id"]?.ToString() ?? request.RefundId, (string?)json?["msg"] ?? "Refund requested") { Status = RefundState.Pending }
            : new RefundResult(false, "", (string?)json?["msg"] ?? "PayU refused the refund.") { Status = RefundState.Failed };
    }

    private static string Clean(string value, int max)
    {
        var s = new string(value.Where(ch => ch != '|').ToArray()).Trim();
        return s.Length <= max ? s : s[..max];
    }
}
