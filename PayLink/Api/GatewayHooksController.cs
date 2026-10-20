using Dapper;
using Microsoft.AspNetCore.Mvc;
using PayLink.Infrastructure;
using PayLink.Payments;
using PayLink.Platform;
using PayLink.Tenancy;
using WebSvc.PayLink.Abstractions;

namespace PayLink.Api;

/// <summary>
/// Notifications from gateways: POST /hooks/{tenant}/{connector}/{test|live}. The tenant is in the URL, so there's no guessing.
/// Every notification is stored first (verified or not); only a verified one changes a payment.
/// </summary>
[ApiController]
[Route("hooks/{slug}/{connectorId}/{mode}")]
public sealed class GatewayHooksController(
    PlatformRepository repo,
    TenantDb tenantDb,
    ConnectorRegistry registry,
    ConnectorSettingsService methods,
    PaymentService payments,
    ILogger<GatewayHooksController> logger) : ControllerBase
{
    [HttpPost]
    public async Task<IActionResult> Receive(string slug, string connectorId, string mode, CancellationToken ct)
    {
        var tenant = await repo.TenantBySlugAsync(slug);
        var connector = registry.Get(connectorId);
        if (tenant is null || connector is null || mode is not ("test" or "live")) return NotFound();
        var paymentMode = mode == "live" ? PaymentMode.Live : PaymentMode.Test;

        Request.EnableBuffering();
        string raw;
        using (var reader = new StreamReader(Request.Body, leaveOpen: true)) raw = await reader.ReadToEndAsync(ct);
        Request.Body.Position = 0;

        GatewayEvent? ev = null;
        string? error = null;
        try
        {
            var config = await methods.ConfigAsync(tenant, connectorId, paymentMode);
            ev = await registry.CallAsync(connector, t => connector.ParseWebhookAsync(Request, config, t), ct);
        }
        catch (Exception ex) when (ex is WebhookVerificationException or ConnectorCallException)
        {
            error = ex.Message;
        }

        await using var c = await tenantDb.OpenAsync(tenant);
        await c.ExecuteAsync(
            "INSERT INTO gateway_events (received_at, connector, mode, verified, gateway_ref, attempt_id, status, raw_body, error) " +
            "VALUES (@at, @connectorId, @mode, @verified, @gatewayRef, @attemptId, @status, @raw, @error);",
            new
            {
                at = Clock.Now(), connectorId, mode, verified = ev is not null, gatewayRef = ev?.GatewayReference, attemptId = ev?.AttemptId,
                status = ev?.Status.ToString().ToLowerInvariant(), raw = raw.Length <= 65000 ? raw : raw[..65000], error,
            });
        if (ev is null)
        {
            logger.LogWarning("Unverified {Connector} notification for {Tenant}: {Error}", connectorId, slug, error);
            return BadRequest(new { error = "verification_failed" });
        }

        var source = new EventSource(new Actor("gateway", connectorId, connector.Manifest.Name, HttpContext.Connection.RemoteIpAddress?.ToString()));
        if (ev.RefundState is not null || ev.RefundId is not null || ev.RefundGatewayReference is not null)
            return Ok(new { received = true, matched = await payments.ApplyRefundEventAsync(c, connectorId, ev, source) });

        var attempt = await c.QueryFirstOrDefaultAsync<Attempt>(
            "SELECT * FROM attempts WHERE connector = @connectorId AND (id = @attemptId OR gateway_ref = @gatewayRef) LIMIT 1;",
            new { connectorId, attemptId = ev.AttemptId ?? "", gatewayRef = ev.GatewayReference });
        var payment = attempt is null ? null : await c.QueryFirstOrDefaultAsync<Payment>("SELECT * FROM payments WHERE id = @id;", new { id = attempt.PaymentId });
        if (attempt is null || payment is null || payment.Mode != paymentMode) return Ok(new { received = true, matched = false });

        await payments.ApplyAsync(c, payment, attempt, new AttemptStatusResult(ev.Status, ev.Reason, ev.GatewayCode, ev.GatewayMessage) { ProviderPaymentId = ev.ProviderPaymentId },
            source, "gateway.notified");
        return Ok(new { received = true, matched = true });
    }
}
