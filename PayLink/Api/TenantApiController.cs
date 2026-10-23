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
public sealed partial class TenantApiController(
    PaymentService payments,
    PlatformSettings settings,
    ConnectorRegistry registry,
    ConnectorSettingsService methods,
    WebhookService webhooks,
    RiskRuleStore risk,
    UpiStatementService statements) : ControllerBase
{
    private TenantRecord Tenant => (TenantRecord)HttpContext.Items[TenantKeyHandler.TenantItem]!;
    private TenantApiKey Key => (TenantApiKey)HttpContext.Items[TenantKeyHandler.KeyItem]!;
    private Actor Me => Actor.Key(Key.Id, Key.Name, HttpContext.Connection.RemoteIpAddress?.ToString());

    // ---------- payments ----------

    /// <summary>Payments, newest first, with the console's filters. Dates are UTC days (yyyy-MM-dd); amounts in paise. Answers total and total_amount for the filter.</summary>
    [HttpGet("payments")]
    [RequireTenantCapability(TenantCapabilities.PaymentsRead)]
    public async Task<IActionResult> Payments(bool livemode = false, string? status = null, string? q = null, string? method = null,
        string? from = null, string? to = null, long? min_amount = null, long? max_amount = null, string? reference = null, int limit = 50, int offset = 0)
    {
        var baseUrl = await settings.GetPublicBaseUrlAsync();
        var filter = Filter(status, q, method, from, to, min_amount, max_amount, reference);
        var list = await payments.SearchAsync(Tenant, livemode, filter, Math.Clamp(limit, 1, 200), Math.Max(0, offset));
        var totals = await payments.TotalsAsync(Tenant, livemode, filter);
        return Json(new { @object = "list", total = totals.Count, total_amount = totals.Amount, data = list.Select(p => PaymentJson.From(p, baseUrl)) });
    }

    /// <summary>Filters shared by the list and the CSV export. Amounts are in paise; dates are UTC days (yyyy-MM-dd), both ends included.</summary>
    private static PaymentFilter Filter(string? status, string? q, string? method, string? from, string? to, long? min, long? max, string? reference) =>
        new(string.IsNullOrEmpty(status) ? null : status, q, string.IsNullOrEmpty(method) ? null : method,
            string.IsNullOrEmpty(from) ? null : DateRange.Parse(null, from, to), min, max, string.IsNullOrEmpty(reference) ? null : reference);

    /// <summary>A payment with its attempts, refunds and full journey (every event, with who did it).</summary>
    [HttpGet("payments/{id}")]
    [RequireTenantCapability(TenantCapabilities.PaymentsRead)]
    public async Task<IActionResult> Payment(string id)
    {
        if (await payments.DetailAsync(Tenant, id) is not { } d) return Error(404, "not_found", $"No payment {id}.");
        var dto = PaymentJson.From(d.Payment, await settings.GetPublicBaseUrlAsync(), d.Attempts, await payments.RefundsAsync(Tenant, id));
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

    /// <summary>Cancels an open payment.</summary>
    [HttpPost("payments/{id}/cancel")]
    [RequireTenantCapability(TenantCapabilities.PaymentsWrite)]
    public async Task<IActionResult> Cancel(string id, [FromBody] NoteBody? body) =>
        Outcome(await payments.CancelAsync(Tenant, id, body?.Note ?? "Cancelled through the tenant developer API", new EventSource(Me)));

    public sealed record RefundBody(long? Amount, string? Reason, bool Manual = false);

    /// <summary>A refund through the gateway, or (manual = true, reason required) a refund the merchant made outside PayLink.</summary>
    [HttpPost("payments/{id}/refunds")]
    [RequireTenantCapability(TenantCapabilities.Refunds)]
    public async Task<IActionResult> Refund(string id, [FromBody] RefundBody body)
    {
        var outcome = await payments.RefundAsync(Tenant, id, body.Amount, body.Reason, Request.Headers["Idempotency-Key"].FirstOrDefault(), new EventSource(Me), body.Manual);
        return outcome.Error is { } e ? Error(e.Status, e.Code, e.Message) : Json(PaymentJson.From(outcome.Refund!), 201);
    }

    // ---------- UPI verification ----------

    /// <summary>PayLink UPI payments waiting for a decision: the UTR the payer entered, and whether there's a screenshot.</summary>
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

    /// <summary>Approves a UPI payment: the money arrived. The note is kept on the journey.</summary>
    [HttpPost("upi/{attemptId}/approve")]
    [RequireTenantCapability(TenantCapabilities.UpiVerify)]
    public async Task<IActionResult> Approve(string attemptId, [FromBody] NoteBody body) =>
        Outcome(await payments.VerifyUpiAsync(Tenant, attemptId, true, body.Note, new EventSource(Me)));

    /// <summary>Rejects a UPI payment: no such credit. The note is kept on the journey.</summary>
    [HttpPost("upi/{attemptId}/reject")]
    [RequireTenantCapability(TenantCapabilities.UpiVerify)]
    public async Task<IActionResult> Reject(string attemptId, [FromBody] NoteBody body) =>
        Outcome(await payments.VerifyUpiAsync(Tenant, attemptId, false, body.Note, new EventSource(Me)));

    /// <summary>UPI payments already approved or rejected (newest first): who decided, when, and the note.</summary>
    [HttpGet("upi/decided")]
    [RequireTenantCapability(TenantCapabilities.UpiVerify)]
    public async Task<IActionResult> UpiDecided(bool livemode = false, string? decision = null, string? q = null, string? from = null, string? to = null, int limit = 50, int offset = 0)
    {
        if (decision is not (null or "approved" or "rejected")) return Error(400, "invalid_request", "decision is approved or rejected.");
        var dates = string.IsNullOrEmpty(from) ? null : DateRange.Parse(null, from, to);
        var (rows, total) = await payments.UpiDecisionsAsync(Tenant, livemode, decision, q, dates, Math.Clamp(limit, 1, 200), Math.Max(0, offset));
        return Json(new
        {
            @object = "list", total,
            data = rows.Select(r => new { payment_id = r.PaymentId, r.Amount, r.Currency, r.Reference, r.Decision, utr = r.Utr, r.Note, decided_by = r.By, decided_by_type = r.ByType, decided_at = r.At }),
        });
    }

    /// <summary>
    /// Matches a bank statement (CSV) against UPI payments waiting for a check: credits whose UTR and amount match are approved.
    /// Send the file as multipart/form-data (field "statement") or as the raw body with Content-Type text/csv.
    /// </summary>
    [HttpPost("upi/statements")]
    [RequireTenantCapability(TenantCapabilities.UpiVerify)]
    [RequestSizeLimit(10 * 1024 * 1024)]
    public async Task<IActionResult> UpiStatement(bool livemode = false)
    {
        string text;
        if (Request.HasFormContentType)
        {
            var file = (await Request.ReadFormAsync()).Files["statement"];
            if (file is not { Length: > 0 }) return Error(400, "invalid_request", "Send the statement CSV in the form field \"statement\".");
            using var reader = new StreamReader(file.OpenReadStream());
            text = await reader.ReadToEndAsync();
        }
        else
        {
            using var reader = new StreamReader(Request.Body);
            text = await reader.ReadToEndAsync();
        }
        if (string.IsNullOrWhiteSpace(text)) return Error(400, "invalid_request", "The statement is empty.");
        var (outcome, problem) = await statements.MatchAsync(Tenant, livemode, text, new EventSource(Me));
        if (problem is not null) return Error(422, "statement_unreadable", problem);
        var o = outcome!;
        return Json(new
        {
            upload_id = o.UploadId, credits = o.Credits, approved = o.Approved, need_a_look = o.NeedALook, not_found = o.NotFound, without_utr = o.NoUtr,
            need_a_look_lines = (await statements.NeedALookAsync(Tenant, o.UploadId))
                .Select(l => new { line = l.LineNo, l.Utr, l.Amount, l.Narration, l.Note }),
        });
    }

    // ---------- payment methods ----------

    /// <summary>Every payment method with its fields and this tenant's Test and Live settings (secrets never returned).</summary>
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

    /// <summary>Saves a method's settings for test or live and, with enabled true, checks them and turns it on. A failed check answers 422.</summary>
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

    /// <summary>Webhook endpoints.</summary>
    [HttpGet("webhooks")]
    [RequireTenantCapability(TenantCapabilities.Webhooks)]
    public async Task<IActionResult> Webhooks() =>
        Json(new { data = (await webhooks.EndpointsAsync(Tenant)).Select(e => new { e.Id, e.Url, e.Livemode, events = e.Events.Split(','), e.Enabled, e.Description, e.CreatedAt }) });

    public sealed record WebhookBody(string Url, bool Livemode, List<string>? Events, string? Description);

    /// <summary>Adds a webhook endpoint. The signing secret (whsec_) is returned once.</summary>
    [HttpPost("webhooks")]
    [RequireTenantCapability(TenantCapabilities.Webhooks)]
    public async Task<IActionResult> AddWebhook([FromBody] WebhookBody body)
    {
        if (WebhookService.UrlProblem(body.Url, body.Livemode) is { } problem) return Error(400, "invalid_request", problem);
        var (endpoint, secret) = await webhooks.AddEndpointAsync(Tenant, body.Url, body.Livemode, body.Events, body.Description, Me);
        return Json(new { endpoint.Id, endpoint.Url, endpoint.Livemode, events = endpoint.Events.Split(','), secret }, 201);
    }

    /// <summary>Removes a webhook endpoint.</summary>
    [HttpDelete("webhooks/{id:long}")]
    [RequireTenantCapability(TenantCapabilities.Webhooks)]
    public async Task<IActionResult> RemoveWebhook(long id) =>
        await webhooks.RemoveEndpointAsync(Tenant, id, Me) ? NoContent() : Error(404, "not_found", $"No webhook endpoint {id}.");

    /// <summary>Recent webhook deliveries, optionally for one payment.</summary>
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

    /// <summary>Sends a delivery again.</summary>
    [HttpPost("webhooks/deliveries/{id:long}/redeliver")]
    [RequireTenantCapability(TenantCapabilities.Webhooks)]
    public async Task<IActionResult> Redeliver(long id) =>
        await webhooks.RedeliverAsync(Tenant, id, Me) ? Accepted() : Error(404, "not_found", $"No delivery {id}.");

    // ---------- risk rules ----------

    /// <summary>Risk rules.</summary>
    [HttpGet("risk-rules")]
    [RequireTenantCapability(TenantCapabilities.Risk)]
    public async Task<IActionResult> Rules() => Json(new { data = await risk.ListAsync(Tenant) });

    public sealed record RuleBody(string Kind, string Value, string Action, string? Note);

    /// <summary>Adds a risk rule: kind, value, action (block or review) and a note.</summary>
    [HttpPost("risk-rules")]
    [RequireTenantCapability(TenantCapabilities.Risk)]
    public async Task<IActionResult> AddRule([FromBody] RuleBody body) =>
        await risk.AddAsync(Tenant, body.Kind, body.Value, body.Action, body.Note, Me) is { } problem
            ? Error(400, "invalid_request", problem)
            : Json(new { added = true }, 201);

    /// <summary>Removes a risk rule.</summary>
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
