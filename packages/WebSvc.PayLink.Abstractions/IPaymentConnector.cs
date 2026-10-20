using Microsoft.AspNetCore.Http;

namespace WebSvc.PayLink.Abstractions;

/// <summary>
/// A payment method or gateway that PayLink can offer at checkout.
/// Gateway-specific details (hash formats, signatures, redirect forms) stay inside the connector;
/// PayLink only ever sees the normalized types in this package.
/// </summary>
public interface IPaymentConnector
{
    ConnectorManifest Manifest { get; }

    /// <summary>Checks that the credentials work against the gateway's Test or Live API.</summary>
    Task<ConnectionCheck> TestConnectionAsync(ConnectorConfig config, CancellationToken cancellationToken);

    /// <summary>Starts one attempt to pay and says what the payer's browser should do next.</summary>
    Task<NextAction> CreateAttemptAsync(AttemptRequest request, ConnectorConfig config, CancellationToken cancellationToken);

    /// <summary>Reads a gateway notification. Must verify the gateway's signature, or throw <see cref="WebhookVerificationException"/>.</summary>
    Task<GatewayEvent> ParseWebhookAsync(HttpRequest request, ConnectorConfig config, CancellationToken cancellationToken);

    /// <summary>Asks the gateway for the current status of an attempt.</summary>
    Task<AttemptStatusResult> FetchStatusAsync(AttemptReference attempt, ConnectorConfig config, CancellationToken cancellationToken);

    /// <summary>Refunds all or part of a succeeded attempt. Only called when the manifest says refunds are supported.</summary>
    Task<RefundResult> RefundAsync(RefundRequest request, ConnectorConfig config, CancellationToken cancellationToken);
}

public sealed class WebhookVerificationException(string message) : Exception(message);

/// <summary>
/// Optional. For gateways that send the payer back to PayLink with the result (a form post or query string, as PayU and
/// CCAvenue do). The connector must verify the data (hash, signature or decryption) or return null, in which case PayLink
/// asks <see cref="IPaymentConnector.FetchStatusAsync"/> instead.
/// </summary>
public interface IPayerReturnHandler
{
    Task<GatewayEvent?> ParseReturnAsync(HttpRequest request, AttemptReference attempt, ConnectorConfig config, CancellationToken cancellationToken);
}

/// <summary>Optional. Lets PayLink ask the gateway how a pending refund ended.</summary>
public interface IRefundStatusFetcher
{
    Task<RefundResult> FetchRefundAsync(string refundId, string gatewayReference, ConnectorConfig config, CancellationToken cancellationToken);
}
