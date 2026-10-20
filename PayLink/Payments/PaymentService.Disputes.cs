using System.Data.Common;
using Dapper;
using PayLink.Infrastructure;
using PayLink.Platform;
using WebSvc.PayLink.Abstractions;

namespace PayLink.Payments;

public sealed class Dispute
{
    public string Id { get; set; } = "";
    public string PaymentId { get; set; } = "";
    public string? AttemptId { get; set; }
    public string Connector { get; set; } = "";
    public bool Livemode { get; set; }
    public string GatewayRef { get; set; } = "";
    public long Amount { get; set; }
    public string Currency { get; set; } = "INR";
    public string? Reason { get; set; }
    public string Status { get; set; } = DisputeStatuses.NeedsResponse;
    public string? RespondBy { get; set; }
    public string? Note { get; set; }
    public string CreatedAt { get; set; } = "";
    public string UpdatedAt { get; set; } = "";

    public bool IsOpen => Status is DisputeStatuses.NeedsResponse or DisputeStatuses.UnderReview;
}

public static class DisputeStatuses
{
    public const string NeedsResponse = "needs_response";
    public const string UnderReview = "under_review";
    public const string Won = "won";
    public const string Lost = "lost";
    public const string Closed = "closed";

    public static string From(DisputeState s) => s switch
    {
        DisputeState.UnderReview => UnderReview,
        DisputeState.Won => Won,
        DisputeState.Lost => Lost,
        DisputeState.Closed => Closed,
        _ => NeedsResponse,
    };

    public static string Label(string s) => s switch
    {
        NeedsResponse => "Needs your response",
        UnderReview => "Under review",
        Won => "Won",
        Lost => "Lost",
        _ => "Closed",
    };
}

public sealed partial class PaymentService
{
    /// <summary>
    /// A gateway reported a dispute (chargeback): matched to the payment by the gateway's payment id, then recorded or updated.
    /// The journey, the merchant's webhooks (dispute.created / .updated / .closed) and the console follow. Evidence goes in on the gateway's dashboard.
    /// </summary>
    public async Task<bool> ApplyDisputeEventAsync(DbConnection c, string connectorId, GatewayEvent ev, EventSource source)
    {
        if (ev.Dispute is not { } info || string.IsNullOrEmpty(info.GatewayReference)) return false;
        var existing = await c.QueryFirstOrDefaultAsync<Dispute>("SELECT * FROM disputes WHERE connector = @connectorId AND gateway_ref = @ref;",
            new { connectorId, @ref = info.GatewayReference });
        var attempt = existing is not null ? null : await c.QueryFirstOrDefaultAsync<Attempt>(
            "SELECT * FROM attempts WHERE connector = @connectorId AND provider_payment_id = @pid AND status = 'succeeded' LIMIT 1;",
            new { connectorId, pid = ev.ProviderPaymentId ?? "" });
        var p = await GetAsync(c, existing?.PaymentId ?? attempt?.PaymentId ?? "");
        if (p is null) return false;

        var status = DisputeStatuses.From(info.State);
        var now = Clock.Now();
        var respondBy = info.RespondBy is { } due ? Clock.Format(due.UtcDateTime) : existing?.RespondBy;
        string type;
        if (existing is null)
        {
            existing = new Dispute
            {
                Id = Ids.New("dsp", 14), PaymentId = p.Id, AttemptId = attempt?.Id, Connector = connectorId, Livemode = p.Livemode, GatewayRef = info.GatewayReference,
                Amount = info.Amount, Currency = string.IsNullOrEmpty(info.Currency) ? p.Currency : info.Currency.ToUpperInvariant(),
                Reason = info.Reason is { Length: > 200 } r ? r[..200] : info.Reason, Status = status, RespondBy = respondBy, CreatedAt = now, UpdatedAt = now,
            };
            await c.ExecuteAsync(
                "INSERT INTO disputes (id, payment_id, attempt_id, connector, livemode, gateway_ref, amount, currency, reason, status, respond_by, created_at, updated_at) " +
                "VALUES (@Id, @PaymentId, @AttemptId, @Connector, @Livemode, @GatewayRef, @Amount, @Currency, @Reason, @Status, @RespondBy, @CreatedAt, @UpdatedAt);", existing);
            type = WebhookEvents.DisputeCreated;
        }
        else
        {
            if (existing.Status == status && existing.RespondBy == respondBy) return true;   // a repeat of what PayLink already has
            existing.Status = status;
            existing.RespondBy = respondBy;
            existing.UpdatedAt = now;
            await c.ExecuteAsync("UPDATE disputes SET status = @Status, respond_by = @RespondBy, updated_at = @UpdatedAt WHERE id = @Id;", existing);
            type = existing.IsOpen ? WebhookEvents.DisputeUpdated : WebhookEvents.DisputeClosed;
        }
        await AppendAsync(c, p, existing.AttemptId, type == WebhookEvents.DisputeCreated ? "dispute.opened" : "dispute." + status, p.Status, p.Status, source,
            new { dispute = existing.Id, amount = existing.Amount, reason = existing.Reason, status, respond_by = existing.RespondBy });
        var baseUrl = await platform.GetPublicBaseUrlAsync();
        await webhooks.EnqueueAsync(c, type, p.Livemode, p.Id, new { dispute = PaymentJson.From(existing), payment = PaymentJson.From(p, baseUrl) });
        return true;
    }

    public async Task<IReadOnlyList<Dispute>> DisputesAsync(TenantRecord tenant, bool livemode, string? paymentId = null)
    {
        await using var c = await tenantDb.OpenAsync(tenant);
        return (await c.QueryAsync<Dispute>(
            $"SELECT * FROM disputes WHERE livemode = @livemode {(paymentId is null ? "" : "AND payment_id = @paymentId")} " +
            "ORDER BY CASE WHEN status IN ('needs_response', 'under_review') THEN 0 ELSE 1 END, respond_by, created_at DESC LIMIT 200;",
            new { livemode, paymentId })).ToList();
    }

    public async Task<bool> NoteDisputeAsync(TenantRecord tenant, string id, string? note, Actor actor)
    {
        note = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
        if (note?.Length > 1000) note = note[..1000];
        await using var c = await tenantDb.OpenAsync(tenant);
        var n = await c.ExecuteAsync("UPDATE disputes SET note = @note, updated_at = @at WHERE id = @id;", new { id, note, at = Clock.Now() });
        if (n > 0) await AuditLog.WriteAsync(c, actor, "dispute.noted", id, note);
        return n > 0;
    }
}
