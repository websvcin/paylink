using Microsoft.AspNetCore.Mvc;
using PayLink.Payments;

namespace PayLink.Pages.Console;

public sealed class ReportsModel(Reports reports) : ConsolePage
{
    /// <summary>Kept for older links (?days=7); "range" and from/to are the current way.</summary>
    [BindProperty(SupportsGet = true)] public int? Days { get; set; }
    [BindProperty(SupportsGet = true)] public string? Range { get; set; }
    [BindProperty(SupportsGet = true)] public string? From { get; set; }
    [BindProperty(SupportsGet = true)] public string? To { get; set; }
    public Reports.Report Report { get; private set; } = null!;
    public DateRange Dates { get; private set; } = null!;

    /// <summary>Reports draw one bar per day, so a range is at most a year.</summary>
    private DateRange Pick() => DateRange.Presets.Any(p => p.Key == Range) ? DateRange.Parse(Range, null, null, maxDays: 366)
        : !string.IsNullOrEmpty(From) ? DateRange.Parse(null, From, To, maxDays: 366)
        : DateRange.Parse(Days?.ToString(), null, null, maxDays: 366);

    public async Task OnGetAsync()
    {
        Dates = Pick();
        Report = await reports.BuildAsync(Tenant, Livemode, Dates);
    }

    public async Task<IActionResult> OnGetCsvAsync()
    {
        Dates = Pick();
        await TenantDb.AuditAsync(Tenant, Me, "report.exported", $"payments, {Dates.Label}, {ModeName}");
        return File(System.Text.Encoding.UTF8.GetBytes(await reports.CsvAsync(Tenant, Livemode, new PaymentFilter(Dates: Dates))), "text/csv",
            $"payments-{Tenant.Slug}-{ModeName}-{Dates.From:yyyyMMdd}-{Dates.To:yyyyMMdd}.csv");
    }
}
