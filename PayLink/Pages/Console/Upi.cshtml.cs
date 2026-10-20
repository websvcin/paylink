using Microsoft.AspNetCore.Mvc;
using PayLink.Payments;

namespace PayLink.Pages.Console;

public sealed class UpiModel(PaymentService payments, UpiStatementService statements) : ConsolePage
{
    public IReadOnlyList<(Payment Payment, Attempt Attempt)> Queue { get; private set; } = [];
    public IReadOnlyList<BankStatementLine> NeedALook { get; private set; } = [];

    public async Task OnGetAsync()
    {
        Queue = await payments.UpiQueueAsync(Tenant, Livemode);
        NeedALook = await statements.NeedALookAsync(Tenant);
    }

    [RequestSizeLimit(10 * 1024 * 1024)]
    public async Task<IActionResult> OnPostStatementAsync(IFormFile? statement)
    {
        if (!CanOperate) return NotAllowed("verify UPI payments");
        if (statement is not { Length: > 0 }) return Failed("Choose your bank statement, exported as CSV.");
        if (statement.Length > 10 * 1024 * 1024) return Failed("The statement is over 10 MB. Export a shorter date range.");
        using var reader = new StreamReader(statement.OpenReadStream());
        var (outcome, problem) = await statements.MatchAsync(Tenant, Livemode, await reader.ReadToEndAsync(), Source);
        if (problem is not null) return Failed(problem);
        var o = outcome!;
        return Done($"{o.Credits} credits read: {o.Approved} UPI payment{(o.Approved == 1 ? "" : "s")} approved, {o.NeedALook} need a look, " +
                    $"{o.NotFound} not PayLink payments{(o.NoUtr > 0 ? $", {o.NoUtr} without a UTR" : "")}.");
    }

    public async Task<IActionResult> OnPostAsync(string attemptId, string decision, string? note)
    {
        if (!CanOperate) return NotAllowed("verify UPI payments");
        var r = await payments.VerifyUpiAsync(Tenant, attemptId, decision == "approve", note, Source);
        return r.Error is { } e ? Failed(e.Message) : Done($"{r.Payment!.Id}: {(decision == "approve" ? "approved" : "rejected")}.");
    }
}
