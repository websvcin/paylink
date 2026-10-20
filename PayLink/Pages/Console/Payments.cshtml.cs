using Microsoft.AspNetCore.Mvc;
using PayLink.Payments;
using PayLink.Platform;

namespace PayLink.Pages.Console;

public sealed class PaymentsModel(PaymentService payments, PlatformSettings settings) : ConsolePage
{
    [BindProperty(SupportsGet = true)] public string? Q { get; set; }
    [BindProperty(SupportsGet = true)] public string? Status { get; set; }
    [BindProperty(SupportsGet = true, Name = "p")] public int PageNo { get; set; } = 1;
    public const int PageSize = 50;

    public IReadOnlyList<Payment> List { get; private set; } = [];

    public async Task OnGetAsync()
    {
        PageNo = Math.Max(1, PageNo);
        List = await payments.SearchAsync(Tenant, Livemode, Status, Q, PageSize + 1, (PageNo - 1) * PageSize);
    }

    /// <summary>A payment made from the console: share its checkout link (no code needed).</summary>
    public async Task<IActionResult> OnPostCreateAsync(string? amount, string? description, string? reference, string? email, string? phone)
    {
        if (!CanOperate) return NotAllowed("create payments");
        if (Money.ParseMajor(amount) is not { } minor) return Failed("Enter an amount in rupees, e.g. 499 or 1,499.50.");
        var result = await payments.CreateAsync(Tenant,
            new CreatePaymentRequest(minor, "INR", reference, description, null, email, phone, null, null, null, null),
            Livemode, null, Source, "console");
        if (result.Error is { } e) return Failed(e.Message);
        TempData["Ok"] = $"Payment created. Share its checkout link: {await settings.GetPublicBaseUrlAsync()}/pay/{result.Payment!.Id}";
        return Redirect($"{Root}/payments/{result.Payment.Id}");
    }
}
