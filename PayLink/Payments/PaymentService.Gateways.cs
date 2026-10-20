using System.Data.Common;
using Dapper;
using PayLink.Infrastructure;
using PayLink.Platform;
using WebSvc.PayLink.Abstractions;

namespace PayLink.Payments;

/// <summary>Gateways (Phase 2): the payer coming back from a gateway, status polling, and refunds.</summary>
public sealed partial class PaymentService
{
    /// <summary>How long after its last check an open gateway attempt is checked again, by its age.</summary>
    public static TimeSpan PollEvery(TimeSpan age) => age < TimeSpan.FromMinutes(30) ? TimeSpan.FromMinutes(1) : TimeSpan.FromMinutes(15);
    public static readonly TimeSpan GiveUpAfter = TimeSpan.FromHours(24);

    private static AttemptReference Reference(Attempt a, Payment p) =>
        new(a.Id, a.GatewayRef ?? a.Id) { ProviderPaymentId = a.ProviderPaymentId, Amount = p.Amount, Currency = p.Currency };

    private static AttemptStatusResult FromEvent(GatewayEvent ev) =>
        new(ev.Status, ev.Reason, ev.GatewayCode, ev.GatewayMessage) { ProviderPaymentId = ev.ProviderPaymentId };

    /// <summary>
    /// The payer came back from the gateway (/pay/{id}/return?attempt=…). A verified result in the request is applied;
    /// otherwise PayLink asks the gateway. Either way the payer then sees the checkout with the outcome.
    /// </summary>
    public async Task ReturnAsync(TenantRecord tenant, string paymentId, string attemptId, HttpRequest request, EventSource source)
    {
        await using var c = await tenantDb.OpenAsync(tenant);
        var p = await GetAsync(c, paymentId);
        var a = p is null ? null : (await AttemptsAsync(c, p.Id)).FirstOrDefault(x => x.Id == attemptId);
        if (p is null || a is null || registry.Get(a.Connector) is not { } connector || AttemptStatuses.IsFinal(a.Status)) return;
        await AppendAsync(c, p, a.Id, "payer.returned", p.Status, p.Status, source, new { connector = a.Connector });

        var config = await methods.ConfigAsync(tenant, a.Connector, p.Mode);
        try
        {
            if (connector is IPayerReturnHandler handler
                && await registry.CallAsync(connector, ct => handler.ParseReturnAsync(request, Reference(a, p), config, ct)) is { } ev)
            {
                await ApplyAsync(c, p, a, FromEvent(ev), source, "gateway.returned");
                return;
            }
            var status = await registry.CallAsync(connector, ct => connector.FetchStatusAsync(Reference(a, p), config, ct));
            if (status.Status != AttemptStatus.RequiresAction) await ApplyAsync(c, p, a, status, EventSource.System, "gateway.status_checked");
        }
        catch (ConnectorCallException ex)
        {
            await AppendAsync(c, p, a.Id, "gateway.unreachable", p.Status, p.Status, EventSource.System, new { message = ex.Message });
        }
    }

    /// <summary>
    /// Asks gateways about attempts still open: every minute for the first half hour, then every 15 minutes, and gives up
    /// (the attempt expires, the payer may try again) after 24 hours. Late webhooks still apply after that.
    /// </summary>
    public async Task<int> PollAsync(TenantRecord tenant, CancellationToken ct = default)
    {
        await using var c = await tenantDb.OpenAsync(tenant);
        var open = (await c.QueryAsync<Attempt>(
            "SELECT * FROM attempts WHERE status IN ('requires_action', 'processing') AND connector NOT IN ('upi', 'simulator') AND created_at < @recent ORDER BY created_at LIMIT 50;",
            new { recent = Clock.Format(DateTime.UtcNow.AddMinutes(-1)) })).ToList();
        var n = 0;
        foreach (var a in open)
        {
            if (ct.IsCancellationRequested) break;
            if (registry.Get(a.Connector) is not { Manifest.Capabilities.StatusPolling: true } connector) continue;
            var age = DateTime.UtcNow - (Clock.Parse(a.CreatedAt) ?? DateTime.UtcNow);
            if (Clock.Parse(a.PolledAt) is { } last && DateTime.UtcNow - last < PollEvery(age)) continue;
            var p = await GetAsync(c, a.PaymentId);
            if (p is null) continue;
            await c.ExecuteAsync("UPDATE attempts SET polled_at = @at WHERE id = @Id;", new { at = Clock.Now(), a.Id });
            try
            {
                var config = await methods.ConfigAsync(tenant, a.Connector, p.Mode);
                var status = await registry.CallAsync(connector, t => connector.FetchStatusAsync(Reference(a, p), config, t), ct);
                var finished = status.Status is AttemptStatus.Succeeded or AttemptStatus.Failed or AttemptStatus.Cancelled or AttemptStatus.Expired;
                var nowProcessing = status.Status == AttemptStatus.Processing && a.Status == AttemptStatuses.RequiresAction;
                if (finished || nowProcessing)
                    await ApplyAsync(c, p, a, status, EventSource.System, "gateway.status_checked");
                else if (age > GiveUpAfter && a.Status == AttemptStatuses.RequiresAction)   // never give up on one that may have taken money
                    await ApplyAsync(c, p, a, new AttemptStatusResult(AttemptStatus.Expired, FailureReason.Expired, "no_result", "No result from the gateway within 24 hours."),
                        EventSource.System, "gateway.gave_up");
                n++;
            }
            catch (ConnectorCallException ex)
            {
                logger.LogInformation("Status check for {Attempt} ({Connector}) failed: {Message}", a.Id, a.Connector, ex.Message);
            }
        }
        return n;
    }

    // ---------- refunds ----------

    public async Task<IReadOnlyList<Refund>> RefundsAsync(TenantRecord tenant, string paymentId)
    {
        await using var c = await tenantDb.OpenAsync(tenant);
        return (await c.QueryAsync<Refund>("SELECT * FROM refunds WHERE payment_id = @paymentId ORDER BY created_at;", new { paymentId })).ToList();
    }

    public sealed record RefundOutcome(Refund? Refund, PaymentError? Error);

    /// <summary>
    /// Refunds all or part of a succeeded payment through its gateway. <paramref name="manual"/> records a refund made
    /// outside PayLink (for PayLink UPI: from the merchant's bank), with a required note. The total refunded never exceeds the payment.
    /// </summary>
    public async Task<RefundOutcome> RefundAsync(TenantRecord tenant, string paymentId, long? amount, string? reason, string? idempotencyKey,
        EventSource source, bool manual = false)
    {
        await using var c = await tenantDb.OpenAsync(tenant);
        var p = await GetAsync(c, paymentId);
        if (p is null) return new(null, new PaymentError(404, "not_found", "Payment not found."));
        if (!string.IsNullOrEmpty(idempotencyKey))
        {
            var again = await c.QueryFirstOrDefaultAsync<Refund>("SELECT * FROM refunds WHERE payment_id = @paymentId AND idempotency_key = @idempotencyKey;", new { paymentId, idempotencyKey });
            if (again is not null) return new(again, null);
        }
        if (p.Status != PaymentStatuses.Succeeded || p.SucceededAttemptId is null) return new(null, new PaymentError(409, "not_paid", "Only succeeded payments can be refunded."));
        var a = (await AttemptsAsync(c, p.Id)).First(x => x.Id == p.SucceededAttemptId);
        var connector = registry.Get(a.Connector);
        if (manual && string.IsNullOrWhiteSpace(reason)) return new(null, new PaymentError(400, "note_required", "Say how and when you refunded it, e.g. \"IMPS from HDFC, ref 1234\"."));
        if (!manual && connector is not { Manifest.Capabilities.Refunds: true })
            return new(null, new PaymentError(409, "refunds_not_supported", $"{connector?.Manifest.Name ?? a.Connector} can't refund through PayLink. Refund the payer yourself, then record it as a manual refund."));

        var pending = await c.ExecuteScalarAsync<long>("SELECT COALESCE(SUM(amount), 0) FROM refunds WHERE payment_id = @paymentId AND status = 'pending';", new { paymentId });
        var left = p.Amount - p.AmountRefunded - pending;
        var value = amount ?? left;
        if (value <= 0 || value > left)
            return new(null, new PaymentError(400, "invalid_amount", $"You can refund up to {Money.Format(left, p.Currency)}{(pending > 0 ? $" ({Money.Format(pending, p.Currency)} is already being refunded)" : "")}."));
        if (!manual && value < p.Amount && connector is { Manifest.Capabilities.PartialRefunds: false })
            return new(null, new PaymentError(400, "partial_not_supported", $"{connector.Manifest.Name} only refunds the full amount."));

        var now = Clock.Now();
        var refund = new Refund
        {
            Id = Ids.New("rf", 14), PaymentId = p.Id, AttemptId = a.Id, Livemode = p.Livemode, Amount = value, Currency = p.Currency,
            Status = RefundStatuses.Pending, Reason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim(), Connector = a.Connector, Manual = manual,
            IdempotencyKey = string.IsNullOrEmpty(idempotencyKey) ? null : idempotencyKey, CreatedBy = source.Actor.Name, CreatedAt = now, UpdatedAt = now,
        };
        await c.ExecuteAsync(
            "INSERT INTO refunds (id, payment_id, attempt_id, livemode, amount, currency, status, reason, connector, manual, idempotency_key, created_by, created_at, updated_at) " +
            "VALUES (@Id, @PaymentId, @AttemptId, @Livemode, @Amount, @Currency, @Status, @Reason, @Connector, @Manual, @IdempotencyKey, @CreatedBy, @CreatedAt, @UpdatedAt);", refund);
        await AppendAsync(c, p, a.Id, "refund.requested", p.Status, p.Status, source,
            new { refund = refund.Id, amount = value, reason = refund.Reason, manual });

        if (manual)
        {
            await FinishRefundAsync(c, p, refund, RefundState.Succeeded, "Recorded by hand", null, source);
            await AuditLog.WriteAsync(c, source.Actor, "refund.recorded", p.Id, $"{Money.Format(value, p.Currency)}: {refund.Reason}");
            return new(refund, null);
        }

        RefundResult result;
        try
        {
            var config = await methods.ConfigAsync(tenant, a.Connector, p.Mode);
            result = await registry.CallAsync(connector!, ct => connector!.RefundAsync(new RefundRequest(refund.Id, Reference(a, p), value, p.Currency, refund.Reason ?? "Refund"), config, ct));
        }
        catch (ConnectorCallException ex)
        {
            result = new RefundResult(false, "", ex.Message) { Status = RefundState.Failed };
        }
        await FinishRefundAsync(c, p, refund, result.Accepted ? result.Status : RefundState.Failed, result.Message, result.GatewayReference, source);
        await AuditLog.WriteAsync(c, source.Actor, "refund.requested", p.Id, $"{Money.Format(value, p.Currency)} via {a.Connector}: {refund.Status}");
        return new(refund, null);
    }

    /// <summary>Moves a refund to its result (only from pending), adds it to the payment's refunded total, records it and tells the merchant.</summary>
    public async Task FinishRefundAsync(DbConnection c, Payment p, Refund refund, RefundState state, string? message, string? gatewayRef, EventSource source)
    {
        var to = state switch { RefundState.Succeeded => RefundStatuses.Succeeded, RefundState.Failed => RefundStatuses.Failed, _ => RefundStatuses.Pending };
        refund.GatewayRef = string.IsNullOrEmpty(gatewayRef) ? refund.GatewayRef : gatewayRef;
        refund.GatewayMessage = message is { Length: > 500 } ? message[..500] : message;
        refund.UpdatedAt = Clock.Now();
        var moved = to != refund.Status && await c.ExecuteAsync(
            "UPDATE refunds SET status = @to, gateway_ref = @GatewayRef, gateway_message = @GatewayMessage, updated_at = @UpdatedAt WHERE id = @Id AND status = 'pending';",
            new { to, refund.GatewayRef, refund.GatewayMessage, refund.UpdatedAt, refund.Id }) > 0;
        if (!moved)
        {
            await c.ExecuteAsync("UPDATE refunds SET gateway_ref = @GatewayRef, gateway_message = @GatewayMessage WHERE id = @Id;", refund);
            return;
        }
        refund.Status = to;
        if (to == RefundStatuses.Succeeded)
        {
            await c.ExecuteAsync("UPDATE payments SET amount_refunded = amount_refunded + @Amount, updated_at = @UpdatedAt WHERE id = @PaymentId;", refund);
            p.AmountRefunded += refund.Amount;
        }
        await AppendAsync(c, p, refund.AttemptId, to == RefundStatuses.Succeeded ? "refund.succeeded" : "refund.failed", p.Status, p.Status, source,
            new { refund = refund.Id, amount = refund.Amount, message = refund.GatewayMessage });
        var baseUrl = await platform.GetPublicBaseUrlAsync();
        await webhooks.EnqueueAsync(c, to == RefundStatuses.Succeeded ? WebhookEvents.RefundSucceeded : WebhookEvents.RefundFailed, p.Livemode, p.Id,
            new { refund = PaymentJson.From(refund), payment = PaymentJson.From(p, baseUrl) });
    }

    /// <summary>A gateway notification about a refund: matched by PayLink's refund id or the gateway's refund reference.</summary>
    public async Task<bool> ApplyRefundEventAsync(DbConnection c, string connectorId, GatewayEvent ev, EventSource source)
    {
        var refund = await c.QueryFirstOrDefaultAsync<Refund>(
            "SELECT * FROM refunds WHERE connector = @connectorId AND (id = @id OR gateway_ref = @gatewayRef) LIMIT 1;",
            new { connectorId, id = ev.RefundId ?? "", gatewayRef = ev.RefundGatewayReference ?? "" });
        if (refund is null || await GetAsync(c, refund.PaymentId) is not { } p) return false;
        await FinishRefundAsync(c, p, refund, ev.RefundState ?? RefundState.Pending, ev.GatewayMessage, ev.RefundGatewayReference, source);
        return true;
    }

    /// <summary>Asks gateways how pending refunds ended (for gateways that can tell).</summary>
    public async Task<int> PollRefundsAsync(TenantRecord tenant, CancellationToken ct = default)
    {
        await using var c = await tenantDb.OpenAsync(tenant);
        var pending = (await c.QueryAsync<Refund>("SELECT * FROM refunds WHERE status = 'pending' AND manual = FALSE AND updated_at < @before LIMIT 50;",
            new { before = Clock.Format(DateTime.UtcNow.AddMinutes(-2)) })).ToList();
        var n = 0;
        foreach (var r in pending)
        {
            if (registry.Get(r.Connector) is not { } connector || connector is not IRefundStatusFetcher fetcher || await GetAsync(c, r.PaymentId) is not { } p) continue;
            try
            {
                var config = await methods.ConfigAsync(tenant, r.Connector, p.Mode);
                var result = await registry.CallAsync(connector, t => fetcher.FetchRefundAsync(r.Id, r.GatewayRef ?? r.Id, config, t), ct);
                if (result.Status != RefundState.Pending) { await FinishRefundAsync(c, p, r, result.Status, result.Message, result.GatewayReference, EventSource.System); n++; }
                else await c.ExecuteAsync("UPDATE refunds SET updated_at = @at WHERE id = @Id;", new { at = Clock.Now(), r.Id });
            }
            catch (ConnectorCallException) { }
        }
        return n;
    }
}
