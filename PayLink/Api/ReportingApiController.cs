using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PayLink.Payments;
using PayLink.Platform;
using PayLink.Tenancy;

namespace PayLink.Api;

/// <summary>
/// The read-only reporting API: GET /api/v1/reporting lists the datasets and their columns, GET /api/v1/reporting/{dataset} reads one
/// with CrudApi's list parameters (filter, orderBy, display, limit, offset, groupBy, count). Secret keys; a test key reads test data,
/// a live key live data.
/// </summary>
[ApiController]
[Route("api/v1/reporting")]
[Authorize(Policy = "PaymentsKey")]
public sealed class ReportingApiController(TenantDb tenantDb) : ControllerBase
{
    private TenantRecord Tenant => (TenantRecord)HttpContext.Items[TenantKeyHandler.TenantItem]!;
    private TenantApiKey Key => (TenantApiKey)HttpContext.Items[TenantKeyHandler.KeyItem]!;

    [HttpGet("")]
    public IActionResult Datasets() => new JsonResult(new
    {
        @object = "list",
        data = ReportingQuery.Datasets.Values.Select(d => new
        {
            name = d.Name,
            columns = d.Columns.Select(c => new { name = c.Key, type = c.Value.ToString().ToLowerInvariant() }),
        }),
    }, PaymentJson.Options);

    [HttpGet("{dataset}")]
    public async Task<IActionResult> Read(string dataset, string? filter, string? orderBy, string? display, int? limit, int? offset, string? groupBy, bool count = false)
    {
        if (!ReportingQuery.Datasets.TryGetValue(dataset.ToLowerInvariant(), out var ds))
            return Error(404, "not_found", $"No dataset {dataset}. Try {string.Join(", ", ReportingQuery.Datasets.Keys)}.");
        try
        {
            await using var c = await tenantDb.OpenAsync(Tenant);
            var result = await ReportingQuery.RunAsync(c, ds, Key.Livemode, filter, orderBy, display, limit, offset, groupBy, count);
            return new JsonResult(new { @object = "list", dataset = ds.Name, livemode = Key.Livemode, data = result.Rows, has_more = result.HasMore, count = result.Count },
                PaymentJson.Options);
        }
        catch (ReportingException ex)
        {
            return Error(400, "invalid_query", ex.Message);
        }
    }

    private ObjectResult Error(int status, string error, string description) => new(new { error, error_description = description }) { StatusCode = status };
}
