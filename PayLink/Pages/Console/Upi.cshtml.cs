using Microsoft.AspNetCore.Mvc;
using PayLink.Payments;

namespace PayLink.Pages.Console;

public sealed class UpiModel(PaymentService payments) : ConsolePage
{
    public IReadOnlyList<(Payment Payment, Attempt Attempt)> Queue { get; private set; } = [];

    public async Task OnGetAsync() => Queue = await payments.UpiQueueAsync(Tenant, Livemode);

    public async Task<IActionResult> OnPostAsync(string attemptId, string decision, string? note)
    {
        if (!CanOperate) return NotAllowed("verify UPI payments");
        var r = await payments.VerifyUpiAsync(Tenant, attemptId, decision == "approve", note, Source);
        return r.Error is { } e ? Failed(e.Message) : Done($"{r.Payment!.Id}: {(decision == "approve" ? "approved" : "rejected")}.");
    }
}
