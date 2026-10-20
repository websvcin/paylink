using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PayLink.Infrastructure;
using PayLink.Payments;
using PayLink.Platform;
using WebSvc.PayLink.Abstractions;

namespace PayLink.Api;

/// <summary>
/// The tenant developer API: automate one tenant's console with a tdk_ key. Each route needs one capability.
/// Test and live data are chosen with ?livemode=true|false (default false).
/// </summary>
[ApiController]
[Route("api/tenant")]
[Authorize(Policy = "TenantDeveloperKey")]
public sealed class TenantApiController(
    PaymentService payments,
    PlatformSettings settings,
    ConnectorRegistry registry,
    ConnectorSettingsService methods,
    WebhookService webhooks,
    RiskRuleStore risk) : ControllerBase
{
    private TenantRecord Tenant => (TenantRecord)HttpContext.Items[TenantKeyHandler.TenantItem]!;
    private TenantApiKey Key => (TenantApiKey)HttpContext.Items[TenantKeyHandler.KeyItem]!;
    private Actor Me => Actor.Key(Key.Id, Key.Name, HttpContext.Connection.RemoteIpAddress?.ToString());

    // ---------- payments ----------

    [HttpGet("payments")]
    [RequireTenantCapability(TenantCapabilities.PaymentsRead)]
    public async Task<IActionResult> Payments(bool livemode = false, string? status = null, string? q = null, int limit = 50, int offset = 0)
    {
        var baseUrl = await settings.GetPublicBaseUrlAsync();
        var list = await payments.SearchAsync(Tenant, livemode, status, q, Math.Clamp(limit, 1, 200), offset);
        return Json(new { @object = "list", data = list.Select(p => PaymentJson.From(p, baseUrl)) });
    }

    [HttpGet("payments/{id}")]
    [RequireTenantCapability(TenantCapabilities.PaymentsRead)]
    public async Task<IActionResult> Payment(string id)
    {
        if (await payments.DetailAsync(Tenant, id) is not { } d) return Error(404, "not_found", $"No payment {id}.");
        var dto = PaymentJson.From(d.Payment, await settings.GetPublicBaseUrlAsync(), d.Attempts);
        return Json(new
        {
            payment = dto,
            journey = d.Events.Select(e => new
            {
                e.At, e.Type, e.StatusBefore, e.StatusAfter, e.AttemptId,
                actor = new { type = e.ActorType, id = e.ActorId, name = e.ActorName, ip = e.Ip, user_agent = e.UserAgent },
                data = e.Data is null ? (object?)null : System.Text.Json.JsonDocument.Parse(e.Data).RootElement,
            }),
        });
    }

    [HttpPost("payments/{id}/cancel")]
    [RequireTenantCapability(TenantCapabilities.PaymentsWrite)]
    public async Task<IActionResult> Cancel(string id, [FromBody] NoteBody? body) =>
        Outcome(await payments.CancelAsync(Tenant, id, body?.Note ?? "Cancelled through the tenant developer API", new EventSource(Me)));

    // ---------- UPI verification ----------

    [HttpGet("upi/pending")]
    [RequireTenantCapability(TenantCapabilities.UpiVerify)]
    public async Task<IActionResult> UpiPending(bool livemode = false) =>
        Json(new
        {
            data = (await payments.UpiQueueAsync(Tenant, livemode)).Select(x => new
            {
                payment_id = x.Payment.Id, attempt_id = x.Attempt.Id, x.Payment.Amount, x.Payment.Currency, x.Payment.Reference,
                utr = x.Attempt.Utr, submitted_at = x.Attempt.UpdatedAt, has_screenshot = x.Attempt.ProofFile is not null,
            }),
        });

    public sealed record NoteBody(string? Note);

    [HttpPost("upi/{attemptId}/approve")]
    [RequireTenantCapability(TenantCapabilities.UpiVerify)]
    public async Task<IActionResult> Approve(string attemptId, [FromBody] NoteBody body) =>
        Outcome(await payments.VerifyUpiAsync(Tenant, attemptId, true, body.Note, new EventSource(Me)));

    [HttpPost("upi/{attemptId}/reject")]
    [RequireTenantCapability(TenantCapabilities.UpiVerify)]
    public async Task<IActionResult> Reject(string attemptId, [FromBody] NoteBody body) =>
        Outcome(await payments.VerifyUpiAsync(Tenant, attemptId, false, body.Note, new EventSource(Me)));

    // ---------- payment methods ----------

    [HttpGet("methods")]
    [RequireTenantCapability(TenantCapabilities.Methods)]
    public async Task<IActionResult> Methods()
    {
        var saved = await methods.ListAsync(Tenant);
        return Json(new
        {
            data = registry.All.Select(c => new
            {
                id = c.Manifest.Id, name = c.Manifest.Name, version = c.Manifest.Version, test_only = c.Manifest.TestOnly,
                methods = c.Manifest.Methods.Select(m => m.ToString().ToLowerInvariant()), currencies = c.Manifest.Currencies,
                fields = c.Manifest.ConfigSchema.Select(f => new { f.Key, f.Label, type = f.Type.ToString().ToLowerInvariant(), f.Required, f.Help }),
                test = Mode(saved, c, "test"), live = Mode(saved, c, "live"),
            }),
        });
    }

    private object? Mode(IReadOnlyList<ConnectorSetting> saved, IPaymentConnector c, string mode)
    {
        var s = saved.FirstOrDefault(x => x.ConnectorId == c.Manifest.Id && x.Mode == mode);
        if (s is null) return null;
        var values = methods.Values(s);
        // Secrets are never returned.
        var shown = c.Manifest.ConfigSchema.Where(f => f.Type != ConfigFieldType.Secret && values.ContainsKey(f.Key)).ToDictionary(f => f.Key, f => values[f.Key]);
        return new { enabled = s.Enabled, check_ok = s.CheckOk, check_message = s.CheckMessage, checked_at = s.CheckedAt, settings = shown };
    }

    public sealed record MethodBody(bool Enabled, Dictionary<string, string?>? Settings);

    [HttpPut("methods/{connectorId}/{mode}")]
    [RequireTenantCapability(TenantCapabilities.Methods)]
    public async Task<IActionResult> SaveMethod(string connectorId, string mode, [FromBody] MethodBody body)
    {
        if (registry.Get(connectorId) is not { } connector) return Error(404, "not_found", $"No connector {connectorId}.");
        if (mode is not ("test" or "live")) return Error(400, "invalid_request", "mode must be test or live.");
        var paymentMode = mode == "live" ? PaymentMode.Live : PaymentMode.Test;
        if (!body.Enabled && (body.Settings is null || body.Settings.Count == 0))
        {
            await methods.DisableAsync(Tenant, connectorId, paymentMode, Me);
            return Json(new { enabled = false });
        }
        var check = await methods.SaveAsync(Tenant, connector, paymentMode, body.Settings ?? new(), body.Enabled, Me);
        return Json(new { enabled = body.Enabled && check.Ok, check_ok = check.Ok, check_message = check.Message }, check.Ok || !body.Enabled ? 200 : 422);
    }

    // ---------- webhooks ----------

    [HttpGet("webhooks")]
    [RequireTenantCapability(TenantCapabilities.Webhooks)]
    public async Task<IActionResult> Webhooks() =>
        Json(new { data = (await webhooks.EndpointsAsync(Tenant)).Select(e => new { e.Id, e.Url, e.Livemode, events = e.Events.Split(','), e.Enabled, e.Description, e.CreatedAt }) });

    public sealed record WebhookBody(string Url, bool Livemode, List<string>? Events, string? Description);

    [HttpPost("webhooks")]
    [RequireTenantCapability(TenantCapabilities.Webhooks)]
    public async Task<IActionResult> AddWebhook([FromBody] WebhookBody body)
    {
        if (WebhookService.UrlProblem(body.Url, body.Livemode) is { } problem) return Error(400, "invalid_request", problem);
        var (endpoint, secret) = await webhooks.AddEndpointAsync(Tenant, body.Url, body.Livemode, body.Events, body.Description, Me);
        return Json(new { endpoint.Id, endpoint.Url, endpoint.Livemode, events = endpoint.Events.Split(','), secret }, 201);
    }

    [HttpDelete("webhooks/{id:long}")]
    [RequireTenantCapability(TenantCapabilities.Webhooks)]
    public async Task<IActionResult> RemoveWebhook(long id) =>
        await webhooks.RemoveEndpointAsync(Tenant, id, Me) ? NoContent() : Error(404, "not_found", $"No webhook endpoint {id}.");

    [HttpGet("webhooks/deliveries")]
    [RequireTenantCapability(TenantCapabilities.Webhooks)]
    public async Task<IActionResult> Deliveries(string? paymentId = null) =>
        Json(new
        {
            data = (await webhooks.DeliveriesAsync(Tenant, paymentId)).Select(d => new
            {
                d.Id, d.EndpointId, d.EventId, d.EventType, d.PaymentId, d.Livemode, d.Status, d.AttemptCount, d.NextAttemptAt,
                d.LastAttemptAt, d.LastStatusCode, d.LastResponse, d.CreatedAt,
            }),
        });

    [HttpPost("webhooks/deliveries/{id:long}/redeliver")]
    [RequireTenantCapability(TenantCapabilities.Webhooks)]
    public async Task<IActionResult> Redeliver(long id) =>
        await webhooks.RedeliverAsync(Tenant, id, Me) ? Accepted() : Error(404, "not_found", $"No delivery {id}.");

    // ---------- risk rules ----------

    [HttpGet("risk-rules")]
    [RequireTenantCapability(TenantCapabilities.Risk)]
    public async Task<IActionResult> Rules() => Json(new { data = await risk.ListAsync(Tenant) });

    public sealed record RuleBody(string Kind, string Value, string Action, string? Note);

    [HttpPost("risk-rules")]
    [RequireTenantCapability(TenantCapabilities.Risk)]
    public async Task<IActionResult> AddRule([FromBody] RuleBody body) =>
        await risk.AddAsync(Tenant, body.Kind, body.Value, body.Action, body.Note, Me) is { } problem
            ? Error(400, "invalid_request", problem)
            : Json(new { added = true }, 201);

    [HttpDelete("risk-rules/{id:long}")]
    [RequireTenantCapability(TenantCapabilities.Risk)]
    public async Task<IActionResult> RemoveRule(long id) =>
        await risk.RemoveAsync(Tenant, id, Me) ? NoContent() : Error(404, "not_found", $"No rule {id}.");

    // ---------- helpers ----------

    private IActionResult Outcome(PaymentResult result)
    {
        if (result.Error is { } e) return Error(e.Status == 400 && result.Payment is not null ? 409 : e.Status, e.Code, e.Message);
        return Json(new { payment = new { result.Payment!.Id, result.Payment.Status } });
    }

    private static JsonResult Json(object value, int status = 200) => new(value, PaymentJson.Options) { StatusCode = status };

    private ObjectResult Error(int status, string error, string description)
    {
        HttpContext.Items[ApiRequestLogging.ErrorItem] = error;
        return new ObjectResult(new { error, error_description = description }) { StatusCode = status };
    }
}
