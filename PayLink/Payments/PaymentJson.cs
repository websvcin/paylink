using System.Text.Json;
using System.Text.Json.Serialization;

namespace PayLink.Payments;

/// <summary>How a payment looks in the Payments API and in webhooks. snake_case, amounts in minor units.</summary>
public static class PaymentJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DictionaryKeyPolicy = null,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public sealed record CustomerDto(string? Name, string? Email, string? Phone, string? Id);
    public sealed record PaidWithDto(string Connector, string? Method, string? Utr);
    public sealed record FailureDto(string Reason, string? Message);
    public sealed record AttemptDto(string Id, string Connector, string Method, string Status, string CreatedAt, string? FailureReason, string? GatewayReference);

    public sealed record PaymentDto(
        string Id, string Object, bool Livemode, string Status, long Amount, string Currency, string? Reference, string? Description,
        CustomerDto? Customer, string? ReturnUrl, IReadOnlyDictionary<string, string>? Metadata, string CheckoutUrl,
        string CreatedAt, string ExpiresAt, string? PaidAt, PaidWithDto? PaidWith, FailureDto? Failure, string? HoldReason,
        IReadOnlyList<AttemptDto>? Attempts);

    public static PaymentDto From(Payment p, string baseUrl, IEnumerable<Attempt>? attempts = null) => new(
        p.Id, "payment", p.Livemode, p.Status, p.Amount, p.Currency, p.Reference, p.Description,
        (p.CustomerName ?? p.CustomerEmail ?? p.CustomerPhone ?? p.CustomerId) is null ? null : new CustomerDto(p.CustomerName, p.CustomerEmail, p.CustomerPhone, p.CustomerId),
        p.ReturnUrl, Metadata(p.Metadata), $"{baseUrl}/pay/{p.Id}",
        p.CreatedAt, p.ExpiresAt, p.PaidAt,
        p.Status == PaymentStatuses.Succeeded && p.Connector is not null ? new PaidWithDto(p.Connector, p.Method, p.Utr) : null,
        string.IsNullOrEmpty(p.FailureReason) ? null : new FailureDto(p.FailureReason, p.FailureMessage),
        p.HoldReason,
        attempts?.Select(a => new AttemptDto(a.Id, a.Connector, a.Method, a.Status, a.CreatedAt, a.FailureReason, a.GatewayRef)).ToList());

    public static IReadOnlyDictionary<string, string>? Metadata(string? json) =>
        string.IsNullOrEmpty(json) ? null : JsonSerializer.Deserialize<Dictionary<string, string>>(json);
}
