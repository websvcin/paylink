using Microsoft.AspNetCore.Mvc;
using PayLink.Payments;

namespace PayLink.Pages.Console;

public sealed class RiskModel(RiskRuleStore rules) : ConsolePage
{
    public IReadOnlyList<RiskRule> Rules { get; private set; } = [];

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
