using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PayLink.Infrastructure;
using PayLink.Payments;
using PayLink.Platform;

namespace PayLink.Api;

/// <summary>
/// The Payments API a merchant's server integrates with once: create a payment, send the payer to checkout_url, trust the webhook.
/// Authenticated with secret keys; an sk_test_ key only ever sees test payments and an sk_live_ key only live ones.
/// JSON is snake_case and amounts are integers in minor units (paise for INR).
/// </summary>
[ApiController]
[Route("api/v1")]
[Authorize(Policy = "PaymentsKey")]
public sealed class PaymentsApiController(PaymentService payments, PayLink.Tenancy.CustomDomains domains) : ControllerBase
{
    private TenantRecord Tenant => (TenantRecord)HttpContext.Items[TenantKeyHandler.TenantItem]!;
    private TenantApiKey Key => (TenantApiKey)HttpContext.Items[TenantKeyHandler.KeyItem]!;
    private EventSource Source => new(Actor.Key(Key.Id, Key.Name, HttpContext.Connection.RemoteIpAddress?.ToString()));

    public sealed class CustomerBody
    {
        public string? Name { get; set; }
        public string? Email { get; set; }
        public string? Phone { get; set; }
        public string? Id { get; set; }
    }

    public sealed class CreateBody
    {
        public long Amount { get; set; }
        public string? Currency { get; set; }
        public string? Reference { get; set; }
        public string? Description { get; set; }
        public CustomerBody? Customer { get; set; }
        public string? ReturnUrl { get; set; }
        public Dictionary<string, string>? Metadata { get; set; }
        [JsonPropertyName("expires_in")] public int? ExpiresIn { get; set; }
    }

    /// <summary>Creates a payment and returns its checkout_url. Send an Idempotency-Key. Amounts in minor units (paise).</summary>
    [HttpPost("payments")]
    public async Task<IActionResult> Create()
    {
        CreateBody? body;
        try { body = await JsonSerializer.DeserializeAsync<CreateBody>(Request.Body, PaymentJson.Options); }
        catch (JsonException ex) { return Error(400, "invalid_json", ex.Message); }
        if (body is null) return Error(400, "invalid_json", "Send a JSON body.");

        var request = new CreatePaymentRequest(body.Amount, body.Currency, body.Reference, body.Description,
            body.Customer?.Name, body.Customer?.Email, body.Customer?.Phone, body.Customer?.Id, body.ReturnUrl, body.Metadata, body.ExpiresIn);
        var result = await payments.CreateAsync(Tenant, request, Key.Livemode, Request.Headers["Idempotency-Key"].FirstOrDefault(), Source, "api", Key.Id);
        if (result.Error is { } e) return Error(e.Status, e.Code, e.Message);
        if (result.Replayed) Response.Headers["Idempotent-Replayed"] = "true";
        return await Shape(result.Payment!, result.Replayed ? 200 : 201);
    }

    /// <summary>A payment: status, attempts, how it was paid (paid_with) or why it failed (failure), refunds.</summary>
    [HttpGet("payments/{id}")]
    public async Task<IActionResult> Get(string id)
    {
        var p = await payments.GetAsync(Tenant, id);
        if (p is null || p.Livemode != Key.Livemode) return Error(404, "not_found", $"No {(Key.Livemode ? "live" : "test")} payment {id}.");
        return await Shape(p, 200);
    }

    /// <summary>Payments for this key's mode, newest first.</summary>
    [HttpGet("payments")]
    public async Task<IActionResult> List(string? status, string? reference, [FromQuery(Name = "link_id")] string? linkId, int limit = 20, int offset = 0)
    {
        var list = await payments.SearchAsync(Tenant, Key.Livemode, status, null, Math.Clamp(limit, 1, 100), offset, reference, linkId);
        var baseUrl = await domains.BaseUrlAsync(Tenant);
        return new JsonResult(new { @object = "list", data = list.Select(p => PaymentJson.From(p, baseUrl)) }, PaymentJson.Options);
    }

    /// <summary>Disputes reported by gateways (Razorpay, Stripe, PayPal); payment_id narrows to one payment.</summary>
    [HttpGet("disputes")]
    public async Task<IActionResult> Disputes([FromQuery(Name = "payment_id")] string? paymentId)
    {
        var list = await payments.DisputesAsync(Tenant, Key.Livemode, string.IsNullOrEmpty(paymentId) ? null : paymentId);
        return new JsonResult(new { @object = "list", data = list.Select(PaymentJson.From) }, PaymentJson.Options);
    }

    /// <summary>Cancels an open payment. A processing payment can't be cancelled: the payer may already have paid.</summary>
    [HttpPost("payments/{id}/cancel")]
    public async Task<IActionResult> Cancel(string id)
    {
        var p = await payments.GetAsync(Tenant, id);
        if (p is null || p.Livemode != Key.Livemode) return Error(404, "not_found", $"No {(Key.Livemode ? "live" : "test")} payment {id}.");
        var result = await payments.CancelAsync(Tenant, id, "Cancelled through the API", Source);
        if (result.Error is { } e) return Error(e.Status == 400 ? 409 : e.Status, e.Code, e.Message);
        return await Shape(result.Payment!, 200);
    }

    public sealed class RefundBody
    {
        public long? Amount { get; set; }
        public string? Reason { get; set; }
    }

    /// <summary>Refunds all (no amount) or part of a succeeded payment through its gateway.</summary>
    [HttpPost("payments/{id}/refunds")]
    public async Task<IActionResult> CreateRefund(string id)
    {
        var p = await payments.GetAsync(Tenant, id);
        if (p is null || p.Livemode != Key.Livemode) return Error(404, "not_found", $"No {(Key.Livemode ? "live" : "test")} payment {id}.");
        RefundBody? body = null;
        if (Request.ContentLength is > 0 || Request.Headers.TransferEncoding.Count > 0)
        {
            try { body = await JsonSerializer.DeserializeAsync<RefundBody>(Request.Body, PaymentJson.Options); }
            catch (JsonException ex) { return Error(400, "invalid_json", ex.Message); }
        }
        var outcome = await payments.RefundAsync(Tenant, id, body?.Amount, body?.Reason, Request.Headers["Idempotency-Key"].FirstOrDefault(), Source);
        if (outcome.Error is { } e) return Error(e.Status, e.Code, e.Message);
        return new JsonResult(PaymentJson.From(outcome.Refund!), PaymentJson.Options) { StatusCode = 201 };
    }

    /// <summary>The payment's refunds.</summary>
    [HttpGet("payments/{id}/refunds")]
    public async Task<IActionResult> Refunds(string id)
    {
        var p = await payments.GetAsync(Tenant, id);
        if (p is null || p.Livemode != Key.Livemode) return Error(404, "not_found", $"No {(Key.Livemode ? "live" : "test")} payment {id}.");
        return new JsonResult(new { @object = "list", data = (await payments.RefundsAsync(Tenant, id)).Select(PaymentJson.From) }, PaymentJson.Options);
    }

    private async Task<IActionResult> Shape(Payment p, int status)
    {
        var attempts = await payments.AttemptsAsync(Tenant, p.Id);
        var refunds = await payments.RefundsAsync(Tenant, p.Id);
        return new JsonResult(PaymentJson.From(p, await domains.BaseUrlAsync(Tenant), attempts, refunds), PaymentJson.Options) { StatusCode = status };
    }

    private ObjectResult Error(int status, string error, string description)
    {
        HttpContext.Items[ApiRequestLogging.ErrorItem] = error;
        return new ObjectResult(new { error, error_description = description }) { StatusCode = status };
    }
}
