using WebSvc.PayLink.Abstractions;

namespace PayLink.Payments;

/// <summary>
/// One status vocabulary for every payment, whatever the gateway. Statuses only move forward; a final status never changes,
/// so a late "failed" notification can't undo a success. The one step back is deliberate: when an attempt fails, the payment
/// returns to requires_action so the payer can try another method (it's still one payment).
/// </summary>
public static class PaymentStatuses
{
    public const string Created = "created";
    public const string OnHold = "on_hold";
    public const string RequiresAction = "requires_action";
    public const string Processing = "processing";
    public const string Succeeded = "succeeded";
    public const string Failed = "failed";
    public const string Cancelled = "cancelled";
    public const string Expired = "expired";

    public static readonly string[] All = [Created, OnHold, RequiresAction, Processing, Succeeded, Failed, Cancelled, Expired];
    public static readonly string[] Open = [Created, OnHold, RequiresAction, Processing];

    public static bool IsFinal(string status) => status is Succeeded or Failed or Cancelled or Expired;

    private static int Rank(string status) => status switch
    {
        Created or OnHold => 0,
        RequiresAction => 1,
        Processing => 2,
        _ => 3,
    };

    public static bool CanMove(string from, string to, bool retry = false)
    {
        if (IsFinal(from) || from == to && to != RequiresAction) return false;
        if (from == OnHold) return to is Created or Cancelled or Expired;
        if (to == OnHold) return from == Created;
        if (retry && to == RequiresAction) return from is RequiresAction or Processing;
        return Rank(to) > Rank(from) || (from == RequiresAction && to == RequiresAction);
    }

    public static string Label(string status) => status switch
    {
        Created => "Created",
        OnHold => "On hold",
        RequiresAction => "Waiting for payer",
        Processing => "Processing",
        Succeeded => "Succeeded",
        Failed => "Failed",
        Cancelled => "Cancelled",
        Expired => "Expired",
        _ => status,
    };

    public static string Tone(string status) => status switch
    {
        Succeeded => "good",
        Failed or Cancelled => "bad",
        Processing or OnHold => "warn",
        Expired => "",
        _ => "info",
    };
}

public static class AttemptStatuses
{
    public const string RequiresAction = "requires_action";
    public const string Processing = "processing";
    public const string Succeeded = "succeeded";
    public const string Failed = "failed";
    public const string Cancelled = "cancelled";
    public const string Expired = "expired";

    public static bool IsFinal(string status) => status is Succeeded or Failed or Cancelled or Expired;

    public static string From(AttemptStatus status) => status switch
    {
        AttemptStatus.RequiresAction => RequiresAction,
        AttemptStatus.Processing => Processing,
        AttemptStatus.Succeeded => Succeeded,
        AttemptStatus.Failed => Failed,
        AttemptStatus.Cancelled => Cancelled,
        _ => Expired,
    };

    public static bool CanMove(string from, string to) =>
        !IsFinal(from) && from != to && !(from == Processing && to == RequiresAction);
}

/// <summary>Normalized failure reasons, stored as snake_case strings.</summary>
public static class FailureReasons
{
    public static string From(FailureReason reason) => reason switch
    {
        FailureReason.None => "",
        FailureReason.PayerCancelled => "payer_cancelled",
        FailureReason.BankDeclined => "bank_declined",
        FailureReason.InsufficientFunds => "insufficient_funds",
        FailureReason.AuthenticationFailed => "authentication_failed",
        FailureReason.UpiTimeout => "upi_timeout",
        FailureReason.GatewayError => "gateway_error",
        FailureReason.RiskBlocked => "risk_blocked",
        FailureReason.Expired => "expired",
        FailureReason.LimitExceeded => "limit_exceeded",
        _ => "unknown",
    };

    public const string RiskBlocked = "risk_blocked";
    public const string Rejected = "rejected_by_merchant";
    public const string TooManyAttempts = "too_many_attempts";
}

public sealed class Payment
{
    public string Id { get; set; } = "";
    public bool Livemode { get; set; }
    public string Status { get; set; } = PaymentStatuses.Created;
    public long Amount { get; set; }
    public string Currency { get; set; } = "INR";
    public string? Reference { get; set; }
    public string? Description { get; set; }
    public string? CustomerName { get; set; }
    public string? CustomerEmail { get; set; }
    public string? CustomerPhone { get; set; }
    public string? CustomerId { get; set; }
    public string? ReturnUrl { get; set; }
    public string? Metadata { get; set; }
    public string? IdempotencyKey { get; set; }
    public string? RequestHash { get; set; }
    public string CreatedVia { get; set; } = "api";
    public string? CreatedBy { get; set; }
    public long? ApiKeyId { get; set; }
    public string TraceId { get; set; } = "";
    public string CreatedAt { get; set; } = "";
    public string UpdatedAt { get; set; } = "";
    public string ExpiresAt { get; set; } = "";
    public string? PaidAt { get; set; }
    public int AttemptCount { get; set; }
    public string? Connector { get; set; }
    public string? Method { get; set; }
    public string? SucceededAttemptId { get; set; }
    public string? Utr { get; set; }
    public string? FailureReason { get; set; }
    public string? FailureMessage { get; set; }
    public string? HoldReason { get; set; }

    public bool IsFinal => PaymentStatuses.IsFinal(Status);
    public PaymentMode Mode => Livemode ? PaymentMode.Live : PaymentMode.Test;
}

public sealed class Attempt
{
    public string Id { get; set; } = "";
    public string PaymentId { get; set; } = "";
    public string Connector { get; set; } = "";
    public string Method { get; set; } = "";
    public string Status { get; set; } = AttemptStatuses.RequiresAction;
    public string? GatewayRef { get; set; }
    public string? NextAction { get; set; }
    public string? FailureReason { get; set; }
    public string? GatewayCode { get; set; }
    public string? GatewayMessage { get; set; }
    public string? Utr { get; set; }
    public string? ProofFile { get; set; }
    public string? Ip { get; set; }
    public string? UserAgent { get; set; }
    public string CreatedAt { get; set; } = "";
    public string UpdatedAt { get; set; } = "";
}

public sealed class PaymentEvent
{
    public long Id { get; set; }
    public string PaymentId { get; set; } = "";
    public string? AttemptId { get; set; }
    public string At { get; set; } = "";
    public string Type { get; set; } = "";
    public string? StatusBefore { get; set; }
    public string? StatusAfter { get; set; }
    public string ActorType { get; set; } = "";
    public string? ActorId { get; set; }
    public string? ActorName { get; set; }
    public string? Ip { get; set; }
    public string? UserAgent { get; set; }
    public string? Data { get; set; }
}

public sealed class ConnectorSetting
{
    public string ConnectorId { get; set; } = "";
    public string Mode { get; set; } = "test";
    public bool Enabled { get; set; }
    public string? ConfigEnc { get; set; }
    public bool? CheckOk { get; set; }
    public string? CheckMessage { get; set; }
    public string? CheckedAt { get; set; }
    public int SortOrder { get; set; }
    public string UpdatedAt { get; set; } = "";
    public string? UpdatedBy { get; set; }
}

public sealed class TenantApiKey
{
    public long Id { get; set; }
    public string Name { get; set; } = "";
    public string Kind { get; set; } = TenantKeyKinds.Secret;
    public bool Livemode { get; set; }
    public string KeyPrefix { get; set; } = "";
    public string KeyHash { get; set; } = "";
    public string? Capabilities { get; set; }
    public string CreatedAt { get; set; } = "";
    public string? CreatedBy { get; set; }
    public string? ExpiresAt { get; set; }
    public string? LastUsedAt { get; set; }
    public string? RevokedAt { get; set; }

    public IReadOnlyList<string> CapabilityList => (Capabilities ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries);
    public bool IsUsable => RevokedAt is null && (ExpiresAt is null || Infrastructure.Clock.Parse(ExpiresAt) > DateTime.UtcNow);
}

public static class TenantKeyKinds
{
    /// <summary>sk_test_… / sk_live_…: the Payments API.</summary>
    public const string Secret = "secret";
    /// <summary>tdk_…: the tenant developer API, limited by capabilities.</summary>
    public const string Developer = "developer";
}

/// <summary>What a tenant developer key (tdk_) may do. Keys are created only from a signed-in console session.</summary>
public static class TenantCapabilities
{
    public const string PaymentsRead = "payments.read";
    public const string PaymentsWrite = "payments.write";
    public const string UpiVerify = "upi.verify";
    public const string Methods = "methods";
    public const string Webhooks = "webhooks";
    public const string Risk = "risk";

    public static readonly (string Key, string Label)[] All =
    [
        (PaymentsRead, "Payments (read): list and search payments, see journeys"),
        (PaymentsWrite, "Payments (write): cancel open payments"),
        (UpiVerify, "UPI verification: approve or reject UTRs"),
        (Methods, "Payment methods: turn on and configure connectors"),
        (Webhooks, "Webhooks: endpoints and redelivery"),
        (Risk, "Risk rules: block lists and limits"),
    ];
}

public sealed class WebhookEndpoint
{
    public long Id { get; set; }
    public string Url { get; set; } = "";
    public bool Livemode { get; set; }
    public string SecretEnc { get; set; } = "";
    public string Events { get; set; } = "*";
    public bool Enabled { get; set; } = true;
    public string? Description { get; set; }
    public string CreatedAt { get; set; } = "";
    public string? CreatedBy { get; set; }

    public bool Wants(string eventType) => Events == "*" || Events.Split(',').Contains(eventType);
}

public sealed class WebhookDelivery
{
    public long Id { get; set; }
    public long EndpointId { get; set; }
    public string EventId { get; set; } = "";
    public string EventType { get; set; } = "";
    public string? PaymentId { get; set; }
    public bool Livemode { get; set; }
    public string Payload { get; set; } = "";
    public string Status { get; set; } = "pending";
    public int AttemptCount { get; set; }
    public string? NextAttemptAt { get; set; }
    public string? LastAttemptAt { get; set; }
    public int? LastStatusCode { get; set; }
    public string? LastResponse { get; set; }
    public int? LastDurationMs { get; set; }
    public string CreatedAt { get; set; } = "";
}

public sealed class ApiRequestLog
{
    public long Id { get; set; }
    public string At { get; set; } = "";
    public long? KeyId { get; set; }
    public string? KeyName { get; set; }
    public bool? Livemode { get; set; }
    public string Method { get; set; } = "";
    public string Path { get; set; } = "";
    public int Status { get; set; }
    public int DurationMs { get; set; }
    public string? IdempotencyKey { get; set; }
    public string? Ip { get; set; }
    public string? Error { get; set; }
}

public sealed class RiskRule
{
    public long Id { get; set; }
    public string Kind { get; set; } = "";
    public string Value { get; set; } = "";
    public string Action { get; set; } = RiskActions.Block;
    public bool Enabled { get; set; } = true;
    public string? Note { get; set; }
    public string CreatedAt { get; set; } = "";
    public string? CreatedBy { get; set; }
}

public static class RiskActions
{
    public const string Block = "block";
    public const string Review = "review";
}

/// <summary>Event types sent to merchants' webhook endpoints.</summary>
public static class WebhookEvents
{
    public const string Processing = "payment.processing";
    public const string OnHold = "payment.on_hold";
    public const string Succeeded = "payment.succeeded";
    public const string Failed = "payment.failed";
    public const string Cancelled = "payment.cancelled";
    public const string Expired = "payment.expired";
    public const string Test = "webhook.test";

    public static readonly string[] All = [Processing, OnHold, Succeeded, Failed, Cancelled, Expired];

    public static string? ForStatus(string status) => status switch
    {
        PaymentStatuses.Processing => Processing,
        PaymentStatuses.OnHold => OnHold,
        PaymentStatuses.Succeeded => Succeeded,
        PaymentStatuses.Failed => Failed,
        PaymentStatuses.Cancelled => Cancelled,
        PaymentStatuses.Expired => Expired,
        _ => null,
    };
}

public static class Ids
{
    public static string New(string prefix, int length = 16) =>
        prefix + "_" + WebSvc.PayLink.Storage.Provisioning.ProvisioningNames.RandomToken(length, includeUpper: true);
}
