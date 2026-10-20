using Microsoft.AspNetCore.Mvc;
using PayLink.Payments;
using QRCoder;

namespace PayLink.Pages.Console;

public sealed class LinksModel(PaymentLinkService links, PayLink.Tenancy.CustomDomains domains) : ConsolePage
{
    public IReadOnlyList<PaymentLink> Links { get; private set; } = [];
    public string BaseUrl { get; private set; } = "";

    public async Task OnGetAsync()
    {
        Links = await links.ListAsync(Tenant, Livemode);
        BaseUrl = await domains.BaseUrlAsync(Tenant);
    }

    public async Task<IActionResult> OnPostAddAsync(string title, string? description, string amountMode, string? amount, string? minAmount, string? maxAmount,
        bool askEmail, bool askPhone, string? returnUrl, int? maxPayments, string? expiresOn, string? code)
    {
        if (!CanOperate) return NotAllowed("create payment links");
        var payer = amountMode == "payer";
        var fixedAmount = payer ? null : Money.ParseMajor(amount);
        if (!payer && fixedAmount is null) return Failed("Enter the amount, e.g. 1,499.00, or let the payer choose it.");
        // A date from the form means the end of that day in India.
        string? expires = DateOnly.TryParse(expiresOn, out var d) ? new DateTimeOffset(d.ToDateTime(new TimeOnly(23, 59, 59)), TimeSpan.FromHours(5.5)).UtcDateTime.ToString("O") : null;
        var (link, problem) = await links.CreateAsync(Tenant, new CreateLinkRequest(title, description, fixedAmount,
            payer ? Money.ParseMajor(minAmount) : null, payer ? Money.ParseMajor(maxAmount) : null, "INR",
            askEmail, askPhone, returnUrl, maxPayments, expires, string.IsNullOrWhiteSpace(code) ? null : code.Trim().ToLowerInvariant()), Livemode, Me);
        return problem is not null ? Failed(problem) : Done($"Link created: {await domains.BaseUrlAsync(Tenant)}/l/{link!.Code}");
    }

    public async Task<IActionResult> OnPostActiveAsync(string id, bool active)
    {
        if (!CanOperate) return NotAllowed("change payment links");
        await links.SetActiveAsync(Tenant, id, active, Me);
        return Done(active ? "Link is taking payments again." : "Link turned off. Payers see that it's no longer taking payments.");
    }

    public async Task<IActionResult> OnGetQrAsync(string id)
    {
        if (await links.GetAsync(Tenant, id) is not { } link) return NotFound();
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode($"{await domains.BaseUrlAsync(Tenant)}/l/{link.Code}", QRCodeGenerator.ECCLevel.M);
        Response.Headers.ContentDisposition = $"attachment; filename=\"paylink-{link.Code}.svg\"";
        return Content(new SvgQRCode(data).GetGraphic(10), "image/svg+xml");
    }
}
