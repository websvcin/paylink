using Microsoft.AspNetCore.Mvc;
using PayLink.Payments;

namespace PayLink.Pages.Console;

public sealed class RiskModel(RiskRuleStore rules) : ConsolePage
{
    public IReadOnlyList<RiskRule> Rules { get; private set; } = [];

    public sealed record Template(string Title, string Why, string Kind, string Value, string Action, bool Editable);

    /// <summary>Ready-made rules most merchants start with.</summary>
    public static readonly Template[] Templates =
    [
        new("Hold large payments", "Someone checks any payment above this amount before it goes through.", "amount_above", "50000", "review", true),
        new("Hold risky checkouts", "Holds attempts whose risk score is above this (0–100).", "score_above", "60", "review", true),
        new("Block throwaway emails", "Blocks payers using mailinator.com, a common throwaway domain.", "email_domain", "mailinator.com", "block", true),
        new("Slow down card testing", "Blocks an IP after this many attempts in one hour.", "ip_attempts_per_hour", "10", "block", true),
        new("Block Tor", "Blocks attempts from Tor exit nodes.", "tor", "yes", "block", false),
        new("Only accept payers in India", "Holds attempts from outside India (needs location data).", "country_allow", "IN", "review", true),
    ];

    public async Task OnGetAsync() => Rules = await rules.ListAsync(Tenant);

    public async Task<IActionResult> OnPostAddAsync(string kind, string value, string action, string? note)
    {
        if (!CanConfigure) return NotAllowed("change risk rules");
        return await rules.AddAsync(Tenant, kind, value, action, note, Me) is { } problem ? Failed(problem) : Done("Rule added. It applies to new payments and attempts.");
    }

    public async Task<IActionResult> OnPostRemoveAsync(long id)
    {
        if (!CanConfigure) return NotAllowed("change risk rules");
        await rules.RemoveAsync(Tenant, id, Me);
        return Done("Rule removed.");
    }
}
