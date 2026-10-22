using Microsoft.AspNetCore.Mvc;
using PayLink.Payments;

namespace PayLink.Pages.Console;

[RequestSizeLimit(50 * 1024 * 1024)]
[RequestFormLimits(MultipartBodyLengthLimit = 50 * 1024 * 1024)]
public sealed class ReconciliationModel(Settlements settlements, ConnectorRegistry registry) : ConsolePage
{
    [BindProperty(SupportsGet = true)] public string? Mapping { get; set; }
    [BindProperty(SupportsGet = true)] public string? Gateway { get; set; }
    public string GatewayName(string? id) => id is null ? "" : registry.Get(id)?.Manifest.Name ?? id;
    public IReadOnlyList<string>? Headers { get; private set; }
    public SettlementMapping? Guess { get; private set; }
    public IReadOnlyList<SettlementBatch> Batches { get; private set; } = [];
    public IReadOnlyList<SettlementLine> Problems { get; private set; } = [];
    public IReadOnlyList<Settlements.CloseDay> Close { get; private set; } = [];
    public IReadOnlyList<Payment> Unsettled { get; private set; } = [];
    public IEnumerable<string> Gateways => registry.All.Where(c => c.Manifest.Id is not ("upi" or "simulator")).Select(c => c.Manifest.Id);

    public async Task OnGetAsync()
    {
        if (Mapping is not null && settlements.StagedHeaders(Tenant.Id, Mapping) is { } h) { Headers = h; Guess = Settlements.Guess(h); }
        Batches = await settlements.BatchesAsync(Tenant);
        Problems = await settlements.ProblemsAsync(Tenant);
        (Close, Unsettled) = await settlements.CloseAsync(Tenant);
    }

    public async Task<IActionResult> OnPostUploadAsync(IFormFile? report, string? connector)
    {
        if (!CanOperate) return NotAllowed("reconcile settlements");
        if (report is null || report.Length == 0) return Failed("Choose the settlement report (.csv) to upload.");
        await using var s = report.OpenReadStream();
        var (token, _, problem) = await settlements.StageAsync(Tenant.Id, s, report.FileName);
        return problem is not null ? Failed(problem) : Redirect($"{Request.Path}?gateway={Uri.EscapeDataString(connector ?? "")}&mapping={token}");
    }

    /// <summary>A settlement report in the shape PayLink reads best, for gateways (or exports) whose own columns don't map well.</summary>
    public IActionResult OnGetTemplate() => File(System.Text.Encoding.UTF8.GetBytes(
        "Payment ID,Amount,Fee,Tax,Net,Settled On,Settlement ID\n" +
        "pay_RZP8Hq2Lx9,1499.00,29.98,5.40,1463.62,2026-10-21,setl_N4x81\n" +
        "pay_KXEHMibRwMv2NJ,500.00,10.00,1.80,488.20,2026-10-21,setl_N4x81\n"), "text/csv", "paylink-settlement-example.csv");

    public async Task<IActionResult> OnPostImportAsync(string token, string? connector, int reference, int gross, int? fee, int? tax, int? net, int? date, int? settlementRef, bool paise)
    {
        if (!CanOperate) return NotAllowed("reconcile settlements");
        static int? Opt(int? i) => i is >= 0 ? i : null;
        var (batch, problem) = await settlements.ImportAsync(Tenant, token, connector,
            new SettlementMapping(reference, gross, Opt(fee), Opt(tax), Opt(net), Opt(date), Opt(settlementRef), paise), Me);
        if (problem is not null) return Failed(problem);
        TempData["Ok"] = $"{batch!.LineCount} lines: {batch.Matched} matched, {batch.Mismatched} need a look, {batch.Unmatched} not found in PayLink.";
        return Redirect(Request.Path);
    }
}
