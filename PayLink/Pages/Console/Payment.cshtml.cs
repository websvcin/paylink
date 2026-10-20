using Microsoft.AspNetCore.Mvc;
using PayLink.Payments;
using PayLink.Platform;

namespace PayLink.Pages.Console;

public sealed class PaymentModel(PaymentService payments, WebhookService webhooks, PlatformSettings settings, FileStore files, ConnectorRegistry registry) : ConsolePage
{
    [FromRoute] public string Id { get; set; } = "";

    public Payment Payment { get; private set; } = null!;
    public IReadOnlyList<Attempt> Attempts { get; private set; } = [];
    public IReadOnlyList<PaymentEvent> Events { get; private set; } = [];
    public IReadOnlyList<WebhookDelivery> Deliveries { get; private set; } = [];
    public string CheckoutUrl { get; private set; } = "";
    public IReadOnlyList<Refund> Refunds { get; private set; } = [];
    public IReadOnlyList<Dispute> Disputes { get; private set; } = [];
    /// <summary>Whether the paying connector can refund through PayLink (otherwise only a manual refund can be recorded).</summary>
    public bool GatewayRefunds { get; private set; }
    public long Refundable { get; private set; }

    public async Task<IActionResult> OnGetAsync()
    {
        if (await payments.DetailAsync(Tenant, Id) is not { } d) return NotFound();
        (Payment, Attempts, Events) = d;
        if (Payment.Livemode != Livemode) ViewData["Livemode"] = Payment.Livemode;   // show this payment's own mode
        Deliveries = await webhooks.DeliveriesAsync(Tenant, Id);
        Refunds = await payments.RefundsAsync(Tenant, Id);
        Disputes = await payments.DisputesAsync(Tenant, Payment.Livemode, Payment.Id);
        GatewayRefunds = Payment.Connector is { } c && registry.Get(c) is { Manifest.Capabilities.Refunds: true };
        Refundable = Payment.Amount - Payment.AmountRefunded - Refunds.Where(r => r.Status == RefundStatuses.Pending).Sum(r => r.Amount);
        CheckoutUrl = $"{await settings.GetPublicBaseUrlAsync()}/pay/{Id}";
        return Page();
    }

    public async Task<IActionResult> OnPostCancelAsync(string? note)
    {
        if (!CanOperate) return NotAllowed("cancel payments");
        var r = await payments.CancelAsync(Tenant, Id, note, Source);
        return r.Error is { } e ? Failed(e.Message) : Done("Payment cancelled.");
    }

    public async Task<IActionResult> OnPostReleaseAsync(string? note)
    {
        if (!CanOperate) return NotAllowed("release held payments");
        var r = await payments.ReleaseHoldAsync(Tenant, Id, note, Source);
        return r.Error is { } e ? Failed(e.Message) : Done("Released. The payer can pay now.");
    }

    public async Task<IActionResult> OnPostVerifyAsync(string attemptId, string decision, string? note)
    {
        if (!CanOperate) return NotAllowed("verify UPI payments");
        var r = await payments.VerifyUpiAsync(Tenant, attemptId, decision == "approve", note, Source);
        return r.Error is { } e ? Failed(e.Message) : Done(decision == "approve" ? "Approved. The payment succeeded." : "Rejected. The payment failed.");
    }

    public async Task<IActionResult> OnPostRefundAsync(string? amount, string? reason, bool manual)
    {
        if (!CanOperate) return NotAllowed("refund payments");
        long? minor = null;
        if (!string.IsNullOrWhiteSpace(amount))
        {
            if (Money.ParseMajor(amount) is not { } m) return Failed("Enter the amount to refund in rupees, or leave it blank to refund everything left.");
            minor = m;
        }
        var outcome = await payments.RefundAsync(Tenant, Id, minor, reason, null, Source, manual);
        if (outcome.Error is { } e) return Failed(e.Message);
        var r = outcome.Refund!;
        return r.Status switch
        {
            RefundStatuses.Succeeded => Done($"Refunded {Money.Format(r.Amount, r.Currency)}."),
            RefundStatuses.Failed => Failed($"The refund failed: {r.GatewayMessage}"),
            _ => Done($"Refund of {Money.Format(r.Amount, r.Currency)} requested. The gateway confirms it later; it shows here."),
        };
    }

    public async Task<IActionResult> OnPostRedeliverAsync(long deliveryId)
    {
        if (!CanDevelop) return NotAllowed("redeliver webhooks");
        await webhooks.RedeliverAsync(Tenant, deliveryId, Me);
        return Done("Queued. It's sent within a few seconds.");
    }

    /// <summary>The payer's UPI screenshot, shown only to signed-in members, and every view is audited.</summary>
    public async Task<IActionResult> OnGetProofAsync(string attemptId)
    {
        var attempt = (await payments.AttemptsAsync(Tenant, Id)).FirstOrDefault(a => a.Id == attemptId);
        if (attempt?.ProofFile is null || await files.OpenAsync(attempt.ProofFile) is not { } file) return NotFound();
        await TenantDb.AuditAsync(Tenant, Me, "file.viewed", Id, $"UPI screenshot for {attemptId}");
        Response.Headers.CacheControl = "private, no-store";
        return File(file.Content, file.ContentType);
    }
}
