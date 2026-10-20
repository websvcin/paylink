using Microsoft.AspNetCore.Mvc;
using PayLink.Payments;

namespace PayLink.Pages.Console;

[RequestSizeLimit(50 * 1024 * 1024)]
[RequestFormLimits(MultipartBodyLengthLimit = 50 * 1024 * 1024)]
public sealed class ReconciliationModel(Settlements settlements, ConnectorRegistry registry) : ConsolePage
{
    [BindProperty(SupportsGet = true)] public string? Mapping { get; set; }
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

    public async Task<IActionResult> OnPostUploadAsync(IFormFile? report)
    {
        if (!CanOperate) return NotAllowed("reconcile settlements");
        if (report is null || report.Length == 0) return Failed("Choose the settlement report (.csv) to upload.");
        await using var s = report.OpenReadStream();
        var (token, _, problem) = await settlements.StageAsync(Tenant.Id, s, report.FileName);
        return problem is not null ? Failed(problem) : Redirect($"{Request.Path}?mapping={token}");
    }

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
