using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PayLink.Infrastructure;
using PayLink.Payments;
using PayLink.Platform;

namespace PayLink.Api;

/// <summary>Payment links over the Payments API: create, list, get, turn off and on. Same keys and JSON style as payments.</summary>
[ApiController]
[Route("api/v1/links")]
[Authorize(Policy = "PaymentsKey")]
public sealed class LinksApiController(PaymentLinkService links, PayLink.Tenancy.CustomDomains domains) : ControllerBase
{
    private TenantRecord Tenant => (TenantRecord)HttpContext.Items[TenantKeyHandler.TenantItem]!;
    private TenantApiKey Key => (TenantApiKey)HttpContext.Items[TenantKeyHandler.KeyItem]!;
    private Actor Me => Actor.Key(Key.Id, Key.Name, HttpContext.Connection.RemoteIpAddress?.ToString());

    public sealed class CreateBody
    {
        public string? Title { get; set; }
        public string? Description { get; set; }
        public long? Amount { get; set; }
        public long? MinAmount { get; set; }
        public long? MaxAmount { get; set; }
        public string? Currency { get; set; }
        public bool AskEmail { get; set; }
        public bool AskPhone { get; set; }
        public string? ReturnUrl { get; set; }
        public int? MaxPayments { get; set; }
        public string? ExpiresAt { get; set; }
        public string? Code { get; set; }
    }

    public sealed record LinkDto(string Id, string Object, bool Livemode, string Code, string Url, string Title, string? Description,
        long? Amount, long? MinAmount, long? MaxAmount, string Currency, bool AskEmail, bool AskPhone, string? ReturnUrl,
        int? MaxPayments, string? ExpiresAt, bool Active, bool Open, long PaymentsSucceeded, long AmountCollected, string CreatedAt);

    /// <summary>Creates a payment link (a reusable page at /l/{code}). Test keys make test links, live keys live ones.</summary>
    [HttpPost("")]
    public async Task<IActionResult> Create()
    {
        CreateBody? body;
        try { body = await JsonSerializer.DeserializeAsync<CreateBody>(Request.Body, PaymentJson.Options); }
        catch (JsonException ex) { return Error(400, "invalid_json", ex.Message); }
        if (body is null) return Error(400, "invalid_json", "Send a JSON body.");
        var (link, problem) = await links.CreateAsync(Tenant, new CreateLinkRequest(body.Title ?? "", body.Description, body.Amount, body.MinAmount, body.MaxAmount,
            body.Currency, body.AskEmail, body.AskPhone, body.ReturnUrl, body.MaxPayments, body.ExpiresAt, body.Code), Key.Livemode, Me);
        if (problem is not null) return Error(problem.Contains("taken") ? 409 : 400, problem.Contains("taken") ? "code_taken" : "invalid_request", problem);
        return await Shape(link!, 201);
    }

    /// <summary>All links for this key's mode.</summary>
    [HttpGet("")]
    public async Task<IActionResult> List()
    {
        var baseUrl = await domains.BaseUrlAsync(Tenant);
        var list = await links.ListAsync(Tenant, Key.Livemode);
        return new JsonResult(new { @object = "list", data = list.Select(l => Dto(l, baseUrl)) }, PaymentJson.Options);
    }

    /// <summary>One link, by id or code.</summary>
    [HttpGet("{id}")]
    public async Task<IActionResult> Get(string id) =>
        await Find(id) is { } l ? await Shape(l, 200) : Error(404, "not_found", $"No {(Key.Livemode ? "live" : "test")} link {id}.");

    /// <summary>Stops the link taking payments.</summary>
    [HttpPost("{id}/deactivate")]
    public Task<IActionResult> Deactivate(string id) => SetActive(id, false);

    /// <summary>Lets the link take payments again.</summary>
    [HttpPost("{id}/activate")]
    public Task<IActionResult> Activate(string id) => SetActive(id, true);

    private async Task<IActionResult> SetActive(string id, bool active)
    {
        if (await Find(id) is not { } l) return Error(404, "not_found", $"No {(Key.Livemode ? "live" : "test")} link {id}.");
        await links.SetActiveAsync(Tenant, l.Id, active, Me);
        return await Shape((await links.GetAsync(Tenant, l.Id))!, 200);
    }

    private async Task<PaymentLink?> Find(string id) => await links.GetAsync(Tenant, id) is { } l && l.Livemode == Key.Livemode ? l : null;

    private LinkDto Dto(PaymentLink l, string baseUrl) => new(l.Id, "link", l.Livemode, l.Code, $"{baseUrl}/l/{l.Code}", l.Title, l.Description,
        l.Amount, l.MinAmount, l.MaxAmount, l.Currency, l.AskEmail, l.AskPhone, l.ReturnUrl, l.MaxPayments, l.ExpiresAt, l.Active,
        PaymentLinkService.Closed(Tenant, l) is null, l.Paid, l.Collected, l.CreatedAt);

    private async Task<IActionResult> Shape(PaymentLink l, int status) =>
        new JsonResult(Dto(l, await domains.BaseUrlAsync(Tenant)), PaymentJson.Options) { StatusCode = status };

    private ObjectResult Error(int status, string error, string description) =>
        new(new { error, error_description = description }) { StatusCode = status };
}
