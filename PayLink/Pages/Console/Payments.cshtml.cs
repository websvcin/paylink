using Microsoft.AspNetCore.Mvc;
using PayLink.Payments;
using PayLink.Platform;

namespace PayLink.Pages.Console;

public sealed class PaymentsModel(PaymentService payments, PlatformSettings settings, Reports reports, ConnectorRegistry registry) : ConsolePage
{
    [BindProperty(SupportsGet = true)] public string? Q { get; set; }
    [BindProperty(SupportsGet = true)] public string? Status { get; set; }
    [BindProperty(SupportsGet = true)] public string? Method { get; set; }
    [BindProperty(SupportsGet = true)] public string? Range { get; set; }
    [BindProperty(SupportsGet = true)] public string? From { get; set; }
    [BindProperty(SupportsGet = true)] public string? To { get; set; }
    [BindProperty(SupportsGet = true)] public string? Min { get; set; }
    [BindProperty(SupportsGet = true)] public string? Max { get; set; }
    [BindProperty(SupportsGet = true, Name = "p")] public int PageNo { get; set; } = 1;
    public const int PageSize = 50;

    public IReadOnlyList<Payment> List { get; private set; } = [];
    public (long Count, long Amount, long Paid) Totals { get; private set; }
    /// <summary>Null means all time (no range chosen).</summary>
    public DateRange? Dates { get; private set; }
    public IReadOnlyList<(string Id, string Name)> Methods => registry.All.Select(c => (c.Manifest.Id, c.Manifest.Name)).ToList();
    public int Pages => (int)Math.Max(1, (Totals.Count + PageSize - 1) / PageSize);

    /// <summary>The current filters as a query string, for paging and the CSV link.</summary>
    public string Query(int? page = null) => string.Join("&", new[]
    {
        $"q={Uri.EscapeDataString(Q ?? "")}", $"status={Status}", $"method={Method}", Dates is null ? "" : Dates.Query, $"min={Min}", $"max={Max}",
        page is { } n ? $"p={n}" : "",
    }.Where(x => x.Length > 0 && !x.EndsWith('=')));

    private PaymentFilter Filter()
    {
        // A quick range pressed wins over dates still filled in; otherwise From/To make a custom range; nothing means all time.
        Dates = DateRange.Presets.Any(p => p.Key == Range) ? DateRange.Parse(Range, null, null)
            : !string.IsNullOrEmpty(From) ? DateRange.Parse(null, From, To)
            : null;
        return new PaymentFilter(Status, Q, string.IsNullOrEmpty(Method) ? null : Method, Dates, Money.ParseMajor(Min), Money.ParseMajor(Max));
    }

    public async Task OnGetAsync()
    {
        PageNo = Math.Max(1, PageNo);
        var filter = Filter();
        Totals = await payments.TotalsAsync(Tenant, Livemode, filter);
        PageNo = Math.Min(PageNo, Pages);
        List = await payments.SearchAsync(Tenant, Livemode, filter, PageSize, (PageNo - 1) * PageSize);
    }

    /// <summary>Exactly what the filters show, as CSV (up to 50,000 rows).</summary>
    public async Task<IActionResult> OnGetCsvAsync()
    {
        var filter = Filter();
        await TenantDb.AuditAsync(Tenant, Me, "report.exported", $"payments, {Query()}, {ModeName}");
        return File(System.Text.Encoding.UTF8.GetBytes(await reports.CsvAsync(Tenant, Livemode, filter)), "text/csv",
            $"payments-{Tenant.Slug}-{ModeName}-{DateTime.UtcNow:yyyyMMdd}.csv");
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
