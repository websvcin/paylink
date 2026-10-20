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
            "risk.blocked" => ($"Blocked by risk rule: {D("kind")} = {D("value")}", "bad"),
            "risk.review" => ($"Held for review by rule: {D("kind")} = {D("value")}", "warn"),
            "risk.released" => ($"Released from review · {D("note")}", "good"),
            "status.ignored" or "attempt.status_ignored" => ($"Ignored a late change to {D("requested")} ({D("because")})", ""),
            _ => (e.Type, ""),
        };
    }

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
