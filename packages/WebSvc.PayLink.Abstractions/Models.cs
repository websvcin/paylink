namespace WebSvc.PayLink.Abstractions;

/// <summary>Describes a connector to the catalog and the tenant console.</summary>
public sealed record ConnectorManifest(
    string Id,
    string Name,
    string Version,
    int AbstractionsMajorVersion,
    IReadOnlyList<PaymentMethodKind> Methods,
    IReadOnlyList<string> Currencies,
    ConnectorCapabilities Capabilities,
    IReadOnlyList<ConfigField> ConfigSchema,
    bool TestOnly = false);

public enum PaymentMethodKind { Upi, Card, NetBanking, Wallet, Emi, PayLater, Other }

public sealed record ConnectorCapabilities(
    bool Refunds,
    bool PartialRefunds,
    bool Webhooks,
    bool StatusPolling);

/// <summary>One field the tenant fills in, separately for Test and Live.</summary>
public sealed record ConfigField(string Key, string Label, ConfigFieldType Type, bool Required, string? Help = null)
{
    /// <summary>For <see cref="ConfigFieldType.Choice"/>: the allowed values (the first is the default).</summary>
    public IReadOnlyList<string>? Options { get; init; }
}

public enum ConfigFieldType { Text, Secret, Url, Boolean, Choice }

public enum PaymentMode { Test, Live }

/// <summary>A tenant's saved settings for one connector in one mode. Secrets are already decrypted.</summary>
public sealed record ConnectorConfig(PaymentMode Mode, IReadOnlyDictionary<string, string> Values, string WebhookUrl);

public sealed record ConnectionCheck(bool Ok, string Message);

/// <summary>Amounts are always integers in minor units (paise for INR).</summary>
public sealed record AttemptRequest(
    string PaymentId,
    string AttemptId,
    long Amount,
    string Currency,
    string Description,
    PayerDetails Payer,
    string ReturnUrl,
    IReadOnlyDictionary<string, string> Metadata);

public sealed record PayerDetails(string? Name, string? Email, string? Phone);

/// <summary>What the payer's browser should do next.</summary>
public abstract record NextAction(string GatewayReference)
{
    public sealed record Redirect(string GatewayReference, string Url) : NextAction(GatewayReference);
    public sealed record PostForm(string GatewayReference, string Url, IReadOnlyDictionary<string, string> Fields) : NextAction(GatewayReference);
    public sealed record ShowQr(string GatewayReference, string Uri) : NextAction(GatewayReference);
    public sealed record Sdk(string GatewayReference, string Payload) : NextAction(GatewayReference);
    public sealed record Failed(string GatewayReference, FailureReason Reason, string GatewayCode, string GatewayMessage) : NextAction(GatewayReference);
}

/// <summary>The normalized status vocabulary every gateway maps onto.</summary>
public enum AttemptStatus { RequiresAction, Processing, Succeeded, Failed, Cancelled, Expired }

/// <summary>Why an attempt failed, independent of the gateway. The gateway's own code is kept alongside.</summary>
public enum FailureReason
{
    None, PayerCancelled, BankDeclined, InsufficientFunds, AuthenticationFailed,
    UpiTimeout, GatewayError, RiskBlocked, Expired, LimitExceeded, Unknown
}

public sealed record AttemptReference(string AttemptId, string GatewayReference)
{
    /// <summary>The gateway's id for the actual payment (e.g. Razorpay's pay_…, PayU's mihpayid), once known. Refunds use it.</summary>
    public string? ProviderPaymentId { get; init; }
    public long Amount { get; init; }
    public string Currency { get; init; } = "INR";
}

public sealed record AttemptStatusResult(AttemptStatus Status, FailureReason Reason, string GatewayCode, string GatewayMessage)
{
    /// <summary>The gateway's id for the payment, when the gateway has one (needed later for refunds).</summary>
    public string? ProviderPaymentId { get; init; }
    /// <summary>How the payer paid, as the gateway reports it (e.g. "upi", "card", "netbanking"), when known.</summary>
    public string? Method { get; init; }
}

public sealed record GatewayEvent(
    string GatewayReference,
    string? AttemptId,
    AttemptStatus Status,
    FailureReason Reason,
    string GatewayCode,
    string GatewayMessage,
    string RawBody)
{
    public string? ProviderPaymentId { get; init; }
    /// <summary>Set when the notification is about a refund rather than a payment attempt.</summary>
    public string? RefundGatewayReference { get; init; }
    /// <summary>PayLink's refund id, when the gateway echoes it back.</summary>
    public string? RefundId { get; init; }
    public RefundState? RefundState { get; init; }
    /// <summary>Set when the notification is about a dispute (chargeback) on a payment; <see cref="ProviderPaymentId"/> names the payment.</summary>
    public DisputeInfo? Dispute { get; init; }
}

/// <summary>A dispute (chargeback) as the gateway reports it. Responding with evidence happens on the gateway's dashboard.</summary>
public sealed record DisputeInfo(string GatewayReference, DisputeState State, long Amount, string Currency, string? Reason)
{
    /// <summary>When the merchant's response is due, if the gateway says.</summary>
    public DateTimeOffset? RespondBy { get; init; }
}

public enum DisputeState { NeedsResponse, UnderReview, Won, Lost, Closed }

public sealed record RefundRequest(string RefundId, AttemptReference Attempt, long Amount, string Currency, string Reason);

public sealed record RefundResult(bool Accepted, string GatewayReference, string Message)
{
    /// <summary>Where the refund stands right after the call. Pending ones finish through a webhook or a later status check.</summary>
    public RefundState Status { get; init; } = RefundState.Pending;
}

public enum RefundState { Pending, Succeeded, Failed }
