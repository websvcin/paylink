using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PayLink.Payments;
using PayLink.Platform;

namespace PayLink.Pages.Links;

/// <summary>A payment link's public page: the payer sees what it's for, enters the amount if the merchant left it open, and goes to checkout.</summary>
public sealed class IndexModel(PaymentLinkService links) : PageModel
{
    [FromRoute] public string Code { get; set; } = "";
    public TenantRecord Tenant { get; private set; } = null!;
    public PaymentLink Link { get; private set; } = null!;
    public string? Closed { get; private set; }

    [BindProperty] public string? Amount { get; set; }
    [BindProperty] public string? Name { get; set; }
    [BindProperty] public string? Email { get; set; }
    [BindProperty] public string? Phone { get; set; }

    private async Task<bool> LoadAsync()
    {
        if (await links.ByCodeAsync(Code) is not { } found) return false;
        (Tenant, Link) = found;
        Closed = PaymentLinkService.Closed(Tenant, Link);
        Response.Headers.CacheControl = "no-store";
        Response.Headers["Referrer-Policy"] = "no-referrer";
        return true;
    }

    public async Task<IActionResult> OnGetAsync() => await LoadAsync() ? Page() : NotFound();

    public async Task<IActionResult> OnPostAsync()
    {
        if (!await LoadAsync()) return NotFound();
        if (Closed is not null) return Page();
        var amount = Link.Amount ?? Money.ParseMajor(Amount);
        if (Link.Amount is null && amount is null) { ModelState.AddModelError("", "Enter the amount to pay, e.g. 1,499.00."); return Page(); }
        var result = await links.PayAsync(Tenant, Link, amount, Name, Email, Phone, EventSource.Payer(HttpContext));
        if (result.Error is { } e) { ModelState.AddModelError("", e.Message); return Page(); }
        return Redirect($"/pay/{result.Payment!.Id}");
    }
}
