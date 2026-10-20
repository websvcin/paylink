using System.Data.Common;
using System.Net;
using Dapper;
using PayLink.Infrastructure;

namespace PayLink.Payments;

public sealed record RiskDecision(string Action, RiskRule? Rule, string Explanation)
{
    public static readonly RiskDecision Allow = new("allow", null, "");
    /// <summary>The checkout's risk score (0–100) and what raised it; set at the attempt stage.</summary>
    public int Score { get; init; }
    public IReadOnlyList<string> Signals { get; init; } = [];
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
        ("country_block", "Payer's country is one of", "checkout", "CN, RU (needs location data)"),
        ("country_allow", "Payer's country is not one of", "checkout", "IN (needs location data)"),
        ("tor", "Payer uses Tor", "checkout", "yes"),
        ("country_phone_mismatch", "Indian phone, but paying from abroad", "checkout", "yes"),
        ("score_above", "Risk score above (0–100)", "checkout", "70"),
    ];

    /// <summary>Signals and their weights in the checkout risk score.</summary>
    public static readonly (string Signal, int Weight, string Meaning)[] ScoreSignals =
    [
        ("new_device", 10, "first payment from this browser"),
        ("recent_failures", 10, "each failed attempt by this payer or IP in the last 24 hours (up to 3)"),
        ("disposable_email", 25, "throwaway email domain"),
        ("country_mismatch", 20, "Indian phone number, IP abroad"),
        ("tor", 40, "Tor exit node"),
        ("unusual_amount", 15, "over 5× this merchant's usual payment"),
        ("burst", 15, "more than 5 attempts from this IP in an hour"),
    ];

    private static readonly HashSet<string> Disposable = new(StringComparer.OrdinalIgnoreCase)
    {
        "mailinator.com", "guerrillamail.com", "10minutemail.com", "tempmail.com", "temp-mail.org", "yopmail.com", "trashmail.com",
        "sharklasers.com", "getnada.com", "dispostable.com", "maildrop.cc", "throwawaymail.com", "fakeinbox.com", "mintemail.com",
        "mohmal.com", "emailondeck.com", "tempr.email", "spamgourmet.com", "mailnesia.com", "moakt.com",
    };

    private static IReadOnlySet<string> Countries(string value) =>
        value.Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries).Select(c => c.Trim().ToUpperInvariant()).ToHashSet();

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
            "country_block" or "country_allow" => Countries(value).All(c => c.Length == 2 && c.All(char.IsLetter)) ? null : "Enter 2-letter country codes, e.g. IN, US.",
            "score_above" => int.TryParse(value, out var sc) && sc is > 0 and < 100 ? null : "Enter a score from 1 to 99.",
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

    /// <summary>Who is paying at the checkout and from where; Country is null when the location data can't tell.</summary>
    public sealed record AttemptContext(Payment Payment, string? Ip, string? Country, bool Tor, bool NewDevice, bool Livemode);

    /// <summary>
    /// When the payer starts an attempt: where they are, how often they've tried, and a risk score from weighted signals.
    /// Rules that need a location fail open when it's unknown.
    /// </summary>
    public static async Task<RiskDecision> OnAttemptAsync(DbConnection c, IEnumerable<RiskRule> rules, AttemptContext ctx)
    {
        var p = ctx.Payment;
        var since = Clock.Format(DateTime.UtcNow.AddHours(-24));
        var hour = Clock.Format(DateTime.UtcNow.AddHours(-1));
        var fromIp = ctx.Ip is null ? 0 : await c.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM attempts WHERE ip = @ip AND created_at > @hour;", new { ip = ctx.Ip, hour });
        var failures = await c.ExecuteScalarAsync<long>(
            "SELECT COUNT(*) FROM attempts a JOIN payments p ON p.id = a.payment_id WHERE a.status = 'failed' AND a.created_at > @since " +
            "AND ((@ip IS NOT NULL AND a.ip = @ip) OR (@email IS NOT NULL AND p.customer_email = @email) OR (@phone IS NOT NULL AND p.customer_phone = @phone));",
            new { since, ip = ctx.Ip, email = p.CustomerEmail, phone = p.CustomerPhone });
        var amounts = (await c.QueryAsync<long>("SELECT amount FROM payments WHERE status = 'succeeded' AND livemode = @livemode ORDER BY created_at DESC LIMIT 100;",
            new { livemode = ctx.Livemode })).OrderBy(a => a).ToList();
        var indianPhone = Digits(p.CustomerPhone) is { } d && (d.StartsWith("91") && d.Length == 12 || d.Length == 10 && d[0] is >= '6' and <= '9');
        var mismatch = indianPhone && ctx.Country is { } country && country != "IN";

        var signals = new List<string>();
        var score = 0;
        void Add(string signal, int times = 1)
        {
            var w = ScoreSignals.First(x => x.Signal == signal).Weight;
            score += w * times;
            signals.Add(times > 1 ? $"{signal}×{times}" : signal);
        }
        if (ctx.NewDevice) Add("new_device");
        if (failures > 0) Add("recent_failures", (int)Math.Min(failures, 3));
        if (p.CustomerEmail?.Split('@').LastOrDefault() is { } domain && Disposable.Contains(domain)) Add("disposable_email");
        if (mismatch) Add("country_mismatch");
        if (ctx.Tor) Add("tor");
        if (amounts.Count >= 10 && p.Amount > 5 * amounts[amounts.Count / 2]) Add("unusual_amount");
        if (fromIp > 5) Add("burst");
        score = Math.Min(score, 100);

        IPAddress.TryParse(ctx.Ip ?? "", out var address);
        var decision = Decide(rules.Where(r => r.Enabled), r => r.Kind switch
        {
            "ip" => address is not null && ParseRange(r.Value) is { } range && range.Contains(address),
            "ip_attempts_per_hour" => int.TryParse(r.Value, out var limit) && fromIp >= limit,
            "country_block" => ctx.Country is { } cb && Countries(r.Value).Contains(cb),
            "country_allow" => ctx.Country is { } ca && !Countries(r.Value).Contains(ca),
            "tor" => ctx.Tor,
            "country_phone_mismatch" => mismatch,
            "score_above" => int.TryParse(r.Value, out var threshold) && score > threshold,
            _ => false,
        });
        return decision with { Score = score, Signals = signals };
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
