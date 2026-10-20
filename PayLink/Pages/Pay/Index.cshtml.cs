using Dapper;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.WebUtilities;
using PayLink.Payments;
using PayLink.Platform;
using QRCoder;
using WebSvc.PayLink.Abstractions;

namespace PayLink.Pages.Pay;

/// <summary>
/// The hosted checkout. The payer needs no account. It lists the methods the merchant turned on for this payment's mode,
/// runs whatever the chosen connector asks for (a QR code, a redirect, an auto-posted form, the Simulator's buttons) and
/// returns the payer to the merchant at the end. If an attempt fails, the payer can try another method: it's still one payment.
/// </summary>
public sealed class IndexModel(PlatformDb platformDb, PlatformRepository repo, PaymentService payments, ConnectorSettingsService methods,
    ConnectorRegistry registry, FileStore files) : PageModel
{
    [FromRoute] public string Id { get; set; } = "";
    [BindProperty(SupportsGet = true)] public bool Choose { get; set; }

    public TenantRecord Tenant { get; private set; } = null!;
    public Payment Payment { get; private set; } = null!;
    public IReadOnlyList<IPaymentConnector> Methods { get; private set; } = [];
    public Attempt? Current { get; private set; }
    public NextAction? Next { get; private set; }
    public string? QrSvg { get; private set; }
    public string? Unavailable { get; private set; }
    public string? Error { get; private set; }

    public string ReturnUrl => Payment.ReturnUrl is null ? "" :
        QueryHelpers.AddQueryString(Payment.ReturnUrl, new Dictionary<string, string?> { ["payment_id"] = Payment.Id, ["status"] = Payment.Status });

    public string ConnectorName(string id) => registry.Get(id)?.Manifest.Name ?? id;

    private async Task<bool> LoadAsync()
    {
        long? tenantId;
        await using (var c = await platformDb.OpenAsync())
            tenantId = await c.ExecuteScalarAsync<long?>("SELECT tenant_id FROM payment_routes WHERE payment_id = @Id;", new { Id });
        if (tenantId is null || await repo.TenantByIdAsync(tenantId.Value) is not { } tenant) return false;
        Tenant = tenant;
        if (!tenant.IsActive) { Unavailable = $"{tenant.Name} can't take payments right now."; }
        if (await payments.FreshAsync(tenant, Id) is not { } p) return false;
        Payment = p;
        Methods = await methods.ForCheckoutAsync(tenant, p);
        var attempts = await payments.AttemptsAsync(tenant, p.Id);
        Current = attempts.LastOrDefault(a => a.Status is AttemptStatuses.RequiresAction or AttemptStatuses.Processing);
        Next = NextActionJson.FromJson(Current?.NextAction);
        if (Next is NextAction.ShowQr qr)
        {
            using var generator = new QRCodeGenerator();
            using var data = generator.CreateQrCode(qr.Uri, QRCodeGenerator.ECCLevel.M);
            QrSvg = new SvgQRCode(data).GetGraphic(4);
        }
        Response.Headers.CacheControl = "no-store";
        Response.Headers["Referrer-Policy"] = "no-referrer";
        return true;
    }

    public async Task<IActionResult> OnGetAsync()
    {
        if (!await LoadAsync()) return NotFound();
        if (Unavailable is null && !Payment.IsFinal) await payments.RecordOpenedAsync(Tenant, Payment, EventSource.Payer(HttpContext));
        return Page();
    }

    public async Task<IActionResult> OnPostChooseAsync(string connector)
    {
        if (!await LoadAsync()) return NotFound();
        if (Unavailable is not null) return Page();
        var (result, next) = await payments.StartAttemptAsync(Tenant, Id, connector, EventSource.Payer(HttpContext));
        if (result.Error is { } e) { TempData["PayError"] = e.Message; return Redirect($"/pay/{Id}"); }
        return next is NextAction.Redirect r ? Redirect(r.Url) : Redirect($"/pay/{Id}");
    }

    public async Task<IActionResult> OnPostUtrAsync(string attemptId, string? utr, IFormFile? screenshot)
    {
        if (!await LoadAsync()) return NotFound();
        string? proof = null;
        if (screenshot is { Length: > 0 })
        {
            var (path, problem) = await files.SaveImageAsync(Tenant.Id, screenshot);
            if (problem is not null) { TempData["PayError"] = problem; return Redirect($"/pay/{Id}"); }
            proof = path;
        }
        var result = await payments.SubmitUtrAsync(Tenant, Id, attemptId, utr, proof, EventSource.Payer(HttpContext));
        if (result.Error is { } e) TempData["PayError"] = e.Message;
        return Redirect($"/pay/{Id}");
    }

    public async Task<IActionResult> OnPostSimulateAsync(string attemptId, string outcome)
    {
        if (!await LoadAsync()) return NotFound();
        var result = await payments.SimulateAsync(Tenant, Id, attemptId, outcome, EventSource.Payer(HttpContext));
        if (result.Error is { } e) TempData["PayError"] = e.Message;
        return Redirect($"/pay/{Id}");
    }
}
