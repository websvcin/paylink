using Microsoft.AspNetCore.Http;
using WebSvc.PayLink.Abstractions;

namespace WebSvc.PayLink.Connector.Simulator;

/// <summary>
/// A test-only connector with no gateway behind it. The checkout shows "Succeed", "Fail" and "Stay pending";
/// PayLink applies the payer's choice itself, so merchants can test their integration and webhooks before
/// signing up with any gateway.
/// </summary>
public sealed class SimulatorConnector : IPaymentConnector
{
    public const string Id = "simulator";

    /// <summary>The <see cref="NextAction.Sdk"/> payload that tells the checkout to show the outcome buttons.</summary>
    public const string ChooseOutcome = "simulator:choose-outcome";

    public static readonly IReadOnlyList<string> Outcomes = ["succeed", "fail", "pending"];

    public ConnectorManifest Manifest { get; } = new(
        Id, "Simulator", "0.1.0", AbstractionsMajorVersion: 0,
        Methods: [PaymentMethodKind.Other],
        Currencies: ["*"],
        Capabilities: new ConnectorCapabilities(Refunds: false, PartialRefunds: false, Webhooks: false, StatusPolling: false),
        ConfigSchema: [],
        TestOnly: true);

    public Task<ConnectionCheck> TestConnectionAsync(ConnectorConfig config, CancellationToken cancellationToken) =>
        Task.FromResult(config.Mode == PaymentMode.Test
            ? new ConnectionCheck(true, "The Simulator needs no settings.")
            : new ConnectionCheck(false, "The Simulator only works in Test mode."));

    public Task<NextAction> CreateAttemptAsync(AttemptRequest request, ConnectorConfig config, CancellationToken cancellationToken) =>
        Task.FromResult<NextAction>(config.Mode == PaymentMode.Test
            ? new NextAction.Sdk("sim_" + request.AttemptId, ChooseOutcome)
            : new NextAction.Failed("", FailureReason.GatewayError, "live_not_supported", "The Simulator only works in Test mode."));

    /// <summary>Maps the payer's choice on the checkout to a normalized status.</summary>
    public static AttemptStatusResult Outcome(string choice) => choice switch
    {
        "succeed" => new(AttemptStatus.Succeeded, FailureReason.None, "simulated_success", "Succeeded in the Simulator."),
        "fail" => new(AttemptStatus.Failed, FailureReason.BankDeclined, "simulated_decline", "Declined in the Simulator."),
        "pending" => new(AttemptStatus.Processing, FailureReason.None, "simulated_pending", "Left pending in the Simulator."),
        _ => throw new ArgumentException($"Unknown Simulator outcome '{choice}'.", nameof(choice)),
    };

    public Task<GatewayEvent> ParseWebhookAsync(HttpRequest request, ConnectorConfig config, CancellationToken cancellationToken) =>
        throw new WebhookVerificationException("The Simulator doesn't send webhooks.");

    public Task<AttemptStatusResult> FetchStatusAsync(AttemptReference attempt, ConnectorConfig config, CancellationToken cancellationToken) =>
        Task.FromResult(new AttemptStatusResult(AttemptStatus.Processing, FailureReason.None, "", "The Simulator has no status to fetch."));

    public Task<RefundResult> RefundAsync(RefundRequest request, ConnectorConfig config, CancellationToken cancellationToken) =>
        Task.FromResult(new RefundResult(false, "", "The Simulator doesn't support refunds."));
}
