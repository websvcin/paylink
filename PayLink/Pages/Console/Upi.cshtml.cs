using Microsoft.AspNetCore.Mvc;
using PayLink.Payments;

namespace PayLink.Pages.Console;

public sealed class UpiModel(PaymentService payments, UpiStatementService statements) : ConsolePage
{
    public IReadOnlyList<(Payment Payment, Attempt Attempt)> Queue { get; private set; } = [];
    public IReadOnlyList<BankStatementLine> NeedALook { get; private set; } = [];
    [BindProperty(SupportsGet = true)] public string View { get; set; } = "waiting";
    [BindProperty(SupportsGet = true)] public string? Decision { get; set; }
    [BindProperty(SupportsGet = true)] public string? Q { get; set; }
    [BindProperty(SupportsGet = true)] public string? Range { get; set; }
    [BindProperty(SupportsGet = true, Name = "p")] public int PageNo { get; set; } = 1;
    public const int PageSize = 50;
    public IReadOnlyList<PaymentService.UpiDecision> Decided { get; private set; } = [];
    public long DecidedTotal { get; private set; }
    public int Pages => (int)Math.Max(1, (DecidedTotal + PageSize - 1) / PageSize);
    public string Query(int page) => $"view=decided&decision={Decision}&q={Uri.EscapeDataString(Q ?? "")}&range={Range}&p={page}";

    public async Task OnGetAsync()
    {
        Queue = await payments.UpiQueueAsync(Tenant, Livemode);
        NeedALook = await statements.NeedALookAsync(Tenant);
        if (View == "decided")
        {
            var dates = DateRange.Presets.Any(p => p.Key == Range) ? DateRange.Parse(Range, null, null) : null;
            (Decided, DecidedTotal) = await payments.UpiDecisionsAsync(Tenant, Livemode, Decision, Q, dates, PageSize, (Math.Max(1, PageNo) - 1) * PageSize);
        }
    }

    /// <summary>A statement in the shape PayLink reads, to compare with your bank's export.</summary>
    public IActionResult OnGetStatementTemplate() => File(System.Text.Encoding.UTF8.GetBytes(
        "Date,Narration,Ref No,Withdrawal,Deposit,Balance\n" +
        "21/10/2026,UPI/412345678901/RAVI KUMAR/ravi@okhdfcbank/Fees,412345678901,,1499.00,25499.00\n" +
        "21/10/2026,UPI-ASHA-asha@ybl-423456789012-Fees,UPI-423456789012,,500.00,25999.00\n" +
        "22/10/2026,NEFT RENT OCTOBER,N123456,8000.00,,17999.00\n"), "text/csv", "paylink-bank-statement-example.csv");

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
