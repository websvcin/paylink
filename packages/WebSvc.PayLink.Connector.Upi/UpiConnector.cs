using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using WebSvc.PayLink.Abstractions;

namespace WebSvc.PayLink.Connector.Upi;

/// <summary>
/// PayLink UPI. The checkout shows a <c>upi://pay</c> QR code (and an "open UPI app" link on phones) that pays
/// straight into the merchant's UPI ID: no gateway and no fee. No bank tells PayLink that the money arrived, so the payer
/// enters the 12-digit UTR from their UPI app and the merchant confirms it in the console's UPI verification queue.
/// </summary>
public sealed partial class UpiConnector : IPaymentConnector
{
    public const string Id = "upi";
    public const string VpaKey = "vpa";
    public const string PayeeNameKey = "payee_name";
    public const string BusinessVpaKey = "business_vpa_confirmed";

    public ConnectorManifest Manifest { get; } = new(
        Id, "PayLink UPI", "0.1.0", AbstractionsMajorVersion: 0,
        Methods: [PaymentMethodKind.Upi],
        Currencies: ["INR"],
        Capabilities: new ConnectorCapabilities(Refunds: false, PartialRefunds: false, Webhooks: false, StatusPolling: false),
        ConfigSchema:
        [
            new ConfigField(VpaKey, "UPI ID (VPA)", ConfigFieldType.Text, Required: true, Help: "Where payers send money, e.g. acme@okhdfcbank. Use a merchant or business UPI ID."),
            new ConfigField(PayeeNameKey, "Name shown to the payer", ConfigFieldType.Text, Required: true, Help: "Usually your registered business name, as it appears in UPI apps."),
            new ConfigField(BusinessVpaKey, "This is a business UPI ID", ConfigFieldType.Boolean, Required: true,
                Help: "Collecting business payments into a personal UPI ID can break your bank's and UPI's terms."),
        ]);

    [GeneratedRegex(@"^[a-zA-Z0-9.\-_]{2,256}@[a-zA-Z][a-zA-Z0-9.\-]{1,64}$")]
    private static partial Regex VpaPattern();

    [GeneratedRegex(@"^\d{12}$")]
    private static partial Regex UtrPattern();

    public static bool IsValidVpa(string? vpa) => vpa is not null && VpaPattern().IsMatch(vpa);

    /// <summary>A UPI transaction reference (UTR / RRN) is 12 digits.</summary>
    public static bool IsValidUtr(string? utr) => utr is not null && UtrPattern().IsMatch(utr);

    public Task<ConnectionCheck> TestConnectionAsync(ConnectorConfig config, CancellationToken cancellationToken)
    {
        config.Values.TryGetValue(VpaKey, out var vpa);
        config.Values.TryGetValue(PayeeNameKey, out var name);
        config.Values.TryGetValue(BusinessVpaKey, out var business);
        if (!IsValidVpa(vpa)) return Task.FromResult(new ConnectionCheck(false, "Enter a UPI ID like name@bank."));
        if (string.IsNullOrWhiteSpace(name)) return Task.FromResult(new ConnectionCheck(false, "Enter the name payers should see."));
        if (business != "true") return Task.FromResult(new ConnectionCheck(false, "Confirm that this is a business UPI ID."));
        return Task.FromResult(new ConnectionCheck(true,
            $"Looks right. Payers will pay {vpa}. PayLink can't check with your bank that it exists, so make a small test payment."));
    }

    public Task<NextAction> CreateAttemptAsync(AttemptRequest request, ConnectorConfig config, CancellationToken cancellationToken)
    {
        if (!string.Equals(request.Currency, "INR", StringComparison.OrdinalIgnoreCase))
            return Task.FromResult<NextAction>(new NextAction.Failed("", FailureReason.GatewayError, "currency_not_supported", "UPI accepts INR only."));
        if (!IsValidVpa(config.Values.GetValueOrDefault(VpaKey)))
            return Task.FromResult<NextAction>(new NextAction.Failed("", FailureReason.GatewayError, "not_configured", "The merchant's UPI ID isn't set up."));
        return Task.FromResult<NextAction>(new NextAction.ShowQr(request.AttemptId, PayUri(request, config)));
    }

    /// <summary>
    /// The NPCI deep link: pa = payee VPA, pn = payee name, am = amount in rupees, cu = currency,
    /// tr = PayLink's attempt id (the merchant's transaction reference), tn = a note carrying the payment id.
    /// </summary>
    public static string PayUri(AttemptRequest request, ConnectorConfig config)
    {
        var amount = (request.Amount / 100m).ToString("0.00", CultureInfo.InvariantCulture);
        var note = Truncate($"{request.PaymentId} {request.Description}".Trim(), 50);
        var query = new[]
        {
            ("pa", config.Values[VpaKey]),
            ("pn", Truncate(config.Values.GetValueOrDefault(PayeeNameKey) ?? "", 50)),
            ("am", amount),
            ("cu", "INR"),
            ("tr", Truncate(request.AttemptId, 35)),
            ("tn", note),
        };
        return "upi://pay?" + string.Join("&", query.Select(q => $"{q.Item1}={Uri.EscapeDataString(q.Item2)}"));
    }

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];

    public Task<GatewayEvent> ParseWebhookAsync(HttpRequest request, ConnectorConfig config, CancellationToken cancellationToken) =>
        throw new WebhookVerificationException("PayLink UPI has no gateway to send webhooks; payments are verified in the console.");

    public Task<AttemptStatusResult> FetchStatusAsync(AttemptReference attempt, ConnectorConfig config, CancellationToken cancellationToken) =>
        Task.FromResult(new AttemptStatusResult(AttemptStatus.Processing, FailureReason.None, "", "Waiting for the merchant to verify the UTR."));

    public Task<RefundResult> RefundAsync(RefundRequest request, ConnectorConfig config, CancellationToken cancellationToken) =>
        Task.FromResult(new RefundResult(false, "", "Refund PayLink UPI payments from your bank or UPI app."));
}
