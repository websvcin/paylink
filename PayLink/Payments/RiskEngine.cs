using System.Data.Common;
using System.Net;
using Dapper;
using PayLink.Infrastructure;

namespace PayLink.Payments;

public sealed record RiskDecision(string Action, RiskRule? Rule, string Explanation)
{
    public static readonly RiskDecision Allow = new("allow", null, "");
    public bool Blocked => Action == RiskActions.Block;
    public bool Review => Action == RiskActions.Review;
}

/// <summary>
/// Basic rules a tenant sets on purpose: block lists, amount limits and how often one IP may try. They run when a payment is created
/// and again when the payer starts an attempt. Every decision is recorded on the payment with the rule that fired; the payer only
/// sees a plain message that never names the rule.
/// </summary>
public static class RiskEngine
{
    public static readonly (string Kind, string Label, string Stage, string Hint)[] Kinds =
    [
        ("email", "Email address", "create", "rahul@example.com"),
        ("email_domain", "Email domain", "create", "mailinator.com"),
        ("phone", "Phone number", "create", "+91 98123 45678"),
        ("customer_id", "Your customer ID", "create", "cus_1234"),
        ("amount_above", "Amount above (₹)", "create", "50000"),
        ("amount_below", "Amount below (₹)", "create", "1"),
        ("ip", "IP address or range", "checkout", "203.0.113.7 or 203.0.113.0/24"),
        ("ip_attempts_per_hour", "Attempts from one IP per hour, more than", "checkout", "10"),
    ];

    public static string? Problem(string kind, string value, string action)
    {
        if (Kinds.All(k => k.Kind != kind)) return "Choose what the rule checks.";
        if (action is not (RiskActions.Block or RiskActions.Review)) return "Choose block or review.";
        if (string.IsNullOrWhiteSpace(value)) return "Enter a value.";
        return kind switch
        {
            "amount_above" or "amount_below" => decimal.TryParse(value, out var a) && a >= 0 ? null : "Enter an amount in rupees, e.g. 50000.",
            "ip_attempts_per_hour" => int.TryParse(value, out var n) && n > 0 ? null : "Enter a whole number, e.g. 10.",
            "ip" => ParseRange(value) is null ? "Enter an IP address like 203.0.113.7 or a range like 203.0.113.0/24." : null,
            "email" => value.Contains('@') ? null : "Enter a full email address.",
            _ => null,
        };
    }

    /// <summary>When the merchant creates the payment: who is paying and how much.</summary>
    public static RiskDecision OnCreate(IEnumerable<RiskRule> rules, Payment p) =>
        Decide(rules.Where(r => r.Enabled), r => r.Kind switch
        {
            "email" => Same(p.CustomerEmail, r.Value),
            "email_domain" => p.CustomerEmail?.Split('@').LastOrDefault() is { } d && Same(d, r.Value.TrimStart('@')),
            "phone" => Digits(p.CustomerPhone) is { Length: >= 10 } a && Digits(r.Value) is { Length: >= 10 } b && a[^10..] == b[^10..],
            "customer_id" => Same(p.CustomerId, r.Value),
            "amount_above" => decimal.TryParse(r.Value, out var max) && p.Amount > max * 100,
            "amount_below" => decimal.TryParse(r.Value, out var min) && p.Amount < min * 100,
            _ => false,
        });

    /// <summary>When the payer starts an attempt on the checkout: where they are, and how often they've tried.</summary>
    public static async Task<RiskDecision> OnAttemptAsync(DbConnection c, IEnumerable<RiskRule> rules, string? ip)
    {
        var enabled = rules.Where(r => r.Enabled && r.Kind is "ip" or "ip_attempts_per_hour").ToList();
        if (enabled.Count == 0 || ip is null || !IPAddress.TryParse(ip, out var address)) return RiskDecision.Allow;
        long? recent = null;
        foreach (var rule in enabled.Where(r => r.Kind == "ip_attempts_per_hour"))
        {
            recent ??= await c.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM attempts WHERE ip = @ip AND created_at > @since;",
                new { ip, since = Clock.Format(DateTime.UtcNow.AddHours(-1)) });
            if (int.TryParse(rule.Value, out var limit) && recent >= limit) return new(rule.Action, rule, $"{recent} attempts from {ip} in the last hour");
        }
        return Decide(enabled, r => r.Kind == "ip" && ParseRange(r.Value) is { } range && range.Contains(address));
    }

    private static RiskDecision Decide(IEnumerable<RiskRule> rules, Func<RiskRule, bool> matches)
    {
        RiskRule? review = null;
        foreach (var rule in rules)
        {
            if (!matches(rule)) continue;
            if (rule.Action == RiskActions.Block) return new(RiskActions.Block, rule, $"{rule.Kind} = {rule.Value}");
            review ??= rule;
        }
        return review is null ? RiskDecision.Allow : new(RiskActions.Review, review, $"{review.Kind} = {review.Value}");
    }

    private static bool Same(string? a, string b) => a is not null && string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);

    private static string? Digits(string? value) => value is null ? null : new string(value.Where(char.IsDigit).ToArray());

    private static IPNetwork? ParseRange(string value)
    {
        value = value.Trim();
        // Only full dotted IPv4 (a.b.c.d) or IPv6; .NET would also read "203.0.113" as an address.
        var address = value.Split('/')[0];
        if (!address.Contains(':') && address.Count(ch => ch == '.') != 3) return null;
        if (!value.Contains('/')) value += IPAddress.TryParse(value, out var single) && single.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? "/128" : "/32";
        return IPNetwork.TryParse(value, out var network) ? network : null;
    }
}
