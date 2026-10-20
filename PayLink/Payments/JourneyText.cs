using System.Text.Json;

namespace PayLink.Payments;

/// <summary>Turns payment events into readable lines for the journey timeline.</summary>
public static class JourneyText
{
    public static (string Title, string Tone) Describe(PaymentEvent e)
    {
        var d = Data(e);
        string D(string key) => d.TryGetValue(key, out var v) ? v : "";
        return e.Type switch
        {
            "payment.created" => ($"Created {(D("via") == "console" ? "in the console" : "through the API")}", "info"),
            "checkout.opened" => ("Payer opened the checkout", ""),
            "attempt.started" => ($"Payer chose {D("connector")}", "info"),
            "attempt.abandoned" => ("Payer switched to another method", ""),
            "attempt.failed" => ($"Attempt failed: {D("reason")}{(D("message") is { Length: > 0 } m ? " · " + m : "")}", "bad"),
            "attempt.cancelled" => ("Attempt cancelled", ""),
            "attempt.expired" => ("Attempt expired", ""),
            "payment.retry_allowed" => ("Payer can try another method", ""),
            "upi.utr_submitted" => ($"Payer entered UTR {D("utr")}{(D("proof") == "true" ? " with a screenshot" : "")}", "warn"),
            "upi.utr_reused" => ($"Payer entered UTR {D("utr")}, already used on {D("other_payment")}: refused", "bad"),
            "payment.processing" => ("Waiting for confirmation", "warn"),
            "upi.approved" => ($"UPI payment approved · {D("note")}", "good"),
            "upi.rejected" => ($"UPI payment rejected · {D("note")}", "bad"),
            "simulator.outcome" => ($"Simulator: payer chose {D("status")}", "info"),
            "gateway.notified" => ($"Gateway reported {D("status")}{(D("code") is { Length: > 0 } c ? " (" + c + ")" : "")}", "info"),
            "payment.succeeded" => ($"Paid with {D("connector")}", "good"),
            "payment.failed" => ("Payment failed", "bad"),
            "payment.cancelled" => ($"Cancelled{(D("note") is { Length: > 0 } n ? " · " + n : "")}", "bad"),
            "payment.expired" => ("Expired before it was paid", ""),
            "payment.too_many_attempts" => ("Stopped: too many attempts", "bad"),
            "risk.scored" => ($"Risk score {D("score")}{(D("country") is { Length: > 0 } co ? " · from " + co : "")}{(D("signals") is { Length: > 2 } sg ? " · " + sg.Trim('[', ']').Replace("\"", "") : "")}", int.TryParse(D("score"), out var sv) && sv >= 50 ? "warn" : ""),
            "risk.blocked" => ($"Blocked by risk rule: {D("kind")} = {D("value")}", "bad"),
            "risk.review" => ($"Held for review by rule: {D("kind")} = {D("value")}", "warn"),
            "risk.released" => ($"Released from review · {D("note")}", "good"),
            "payer.returned" => ($"Payer came back from {D("connector")}", ""),
            "gateway.returned" => ($"{D("status") switch { "succeeded" => "Paid", "failed" => "Failed", "cancelled" => "Cancelled", _ => "Returned: " + D("status") }} at the gateway{(D("code") is { Length: > 0 } rc ? " (" + rc + ")" : "")}", "info"),
            "gateway.status_checked" => ($"Gateway status: {D("status")}{(D("code") is { Length: > 0 } sc ? " (" + sc + ")" : "")}", "info"),
            "gateway.unreachable" => ($"Couldn't reach the gateway: {D("message")}", "warn"),
            "gateway.gave_up" => ("No result from the gateway within 24 hours", ""),
            "refund.requested" => ($"Refund of {Amount(D("amount"))} requested{(D("manual") == "true" ? " (recorded by hand)" : "")}{(D("reason") is { Length: > 0 } rr ? " · " + rr : "")}", "warn"),
            "refund.succeeded" => ($"Refunded {Amount(D("amount"))}", "good"),
            "refund.failed" => ($"Refund failed{(D("message") is { Length: > 0 } fm ? ": " + fm : "")}", "bad"),
            "status.ignored" or "attempt.status_ignored" => ($"Ignored a late change to {D("requested")} ({D("because")})", ""),
            _ => (e.Type, ""),
        };
    }

    private static string Amount(string minor) => long.TryParse(minor, out var m) ? Money.Format(m, "INR") : minor;

    public static string Actor(PaymentEvent e) => e.ActorType switch
    {
        "payer" => "Payer",
        "key" => $"API key {e.ActorName}",
        "person" => e.ActorName ?? "Team member",
        "gateway" => e.ActorName ?? "Gateway",
        "system" => "PayLink",
        _ => e.ActorName ?? e.ActorType,
    };

    private static Dictionary<string, string> Data(PaymentEvent e)
    {
        if (string.IsNullOrEmpty(e.Data)) return new();
        try
        {
            using var doc = JsonDocument.Parse(e.Data);
            return doc.RootElement.ValueKind != JsonValueKind.Object ? new()
                : doc.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() ?? "" : p.Value.ToString());
        }
        catch (JsonException) { return new(); }
    }
}
