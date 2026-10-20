using WebSvc.PayLink.Abstractions;
using WebSvc.PayLink.Connector.CCAvenue;
using WebSvc.PayLink.Connector.PayU;
using WebSvc.PayLink.Connector.PhonePe;
using WebSvc.PayLink.Connector.Razorpay;
using WebSvc.PayLink.Connector.Simulator;
using WebSvc.PayLink.Connector.Upi;

namespace PayLink.Payments;

/// <summary>
/// The payment connectors in this build. Each comes from its own NuGet package and depends only on WebSvc.PayLink.Abstractions.
/// Every call goes through <see cref="CallAsync{T}"/>, so a slow or broken connector only fails its own attempts.
/// </summary>
public sealed class ConnectorRegistry(IHttpClientFactory httpFactory, ILogger<ConnectorRegistry> logger)
{
    public const string HttpClientName = "gateways";
    public static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(20);

    private IReadOnlyList<IPaymentConnector>? _connectors;

    private IReadOnlyList<IPaymentConnector> Connectors => _connectors ??=
    [
        new UpiConnector(),
        new RazorpayConnector(httpFactory.CreateClient(HttpClientName)),
        new PhonePeConnector(httpFactory.CreateClient(HttpClientName)),
        new PayUConnector(httpFactory.CreateClient(HttpClientName)),
        new CCAvenueConnector(httpFactory.CreateClient(HttpClientName)),
        new SimulatorConnector(),
    ];

    public IReadOnlyList<IPaymentConnector> All => Connectors;

    public IPaymentConnector? Get(string id) => Connectors.FirstOrDefault(c => c.Manifest.Id == id);

    public static bool Supports(ConnectorManifest manifest, string currency) =>
        manifest.Currencies.Contains("*") || manifest.Currencies.Contains(currency, StringComparer.OrdinalIgnoreCase);

    public static string MethodOf(ConnectorManifest manifest) => manifest.Methods.Count == 1
        ? manifest.Methods[0].ToString().ToLowerInvariant()
        : "other";

    /// <summary>Runs one connector call with a timeout; any exception becomes a <see cref="ConnectorCallException"/>.</summary>
    public async Task<T> CallAsync<T>(IPaymentConnector connector, Func<CancellationToken, Task<T>> call, CancellationToken ct = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(CallTimeout);
        try
        {
            return await call(timeout.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning("Connector {Connector} timed out", connector.Manifest.Id);
            throw new ConnectorCallException("timeout", $"{connector.Manifest.Name} didn't answer within {CallTimeout.TotalSeconds:0} seconds.");
        }
        catch (WebhookVerificationException) { throw; }
        catch (Exception ex) when (ex is not ConnectorCallException)
        {
            logger.LogError(ex, "Connector {Connector} failed", connector.Manifest.Id);
            throw new ConnectorCallException("connector_error", $"{connector.Manifest.Name} failed: {ex.Message}");
        }
    }
}

public sealed class ConnectorCallException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}
