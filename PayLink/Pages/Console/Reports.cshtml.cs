using Microsoft.AspNetCore.Mvc;
using PayLink.Payments;

namespace PayLink.Pages.Console;

public sealed class ReportsModel(Reports reports) : ConsolePage
{
    [BindProperty(SupportsGet = true)] public int Days { get; set; } = 30;
    public Reports.Report Report { get; private set; } = null!;

    private int Range => Days is 7 or 30 or 90 ? Days : 30;

    public async Task OnGetAsync()
    {
        Days = Range;
        Report = await reports.BuildAsync(Tenant, Livemode, Days);
    }

    public async Task<IActionResult> OnGetCsvAsync()
    {
        await TenantDb.AuditAsync(Tenant, Me, "report.exported", $"payments, {Range} days, {ModeName}");
        return File(System.Text.Encoding.UTF8.GetBytes(await reports.CsvAsync(Tenant, Livemode, Range)), "text/csv",
            $"payments-{Tenant.Slug}-{ModeName}-{DateTime.UtcNow:yyyyMMdd}.csv");
    }
}
