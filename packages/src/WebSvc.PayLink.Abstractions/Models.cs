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
public sealed record ConfigField(string Key, string Label, ConfigFieldType Type, bool Required, string? Help = null);

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

public sealed record AttemptReference(string AttemptId, string GatewayReference);

public sealed record AttemptStatusResult(AttemptStatus Status, FailureReason Reason, string GatewayCode, string GatewayMessage);

public sealed record GatewayEvent(
    string GatewayReference,
    string? AttemptId,
    AttemptStatus Status,
    FailureReason Reason,
    string GatewayCode,
    string GatewayMessage,
    string RawBody);

public sealed record RefundRequest(string RefundId, AttemptReference Attempt, long Amount, string Currency, string Reason);

public sealed record RefundResult(bool Accepted, string GatewayReference, string Message);
