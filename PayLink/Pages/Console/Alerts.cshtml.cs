using Microsoft.AspNetCore.Mvc;
using PayLink.Payments;

namespace PayLink.Pages.Console;

public sealed class AlertsModel(AlertService alerts) : ConsolePage
{
    public IReadOnlyList<AlertRule> Rules { get; private set; } = [];
    public IReadOnlyList<AlertEvent> Recent { get; private set; } = [];

    public async Task OnGetAsync()
    {
        Rules = await alerts.RulesAsync(Tenant);
        Recent = await alerts.RecentAsync(Tenant);
    }

    public async Task<IActionResult> OnPostAddAsync(string kind, string threshold, int windowMinutes, string channel)
    {
        if (!CanConfigure) return NotAllowed("change alerts");
        return await alerts.AddAsync(Tenant, kind, threshold, windowMinutes, channel, Me) is { } problem ? Failed(problem) : Done("Alert added.");
    }

    public async Task<IActionResult> OnPostRemoveAsync(long id)
    {
        if (!CanConfigure) return NotAllowed("change alerts");
        await alerts.RemoveAsync(Tenant, id, Me);
        return Done("Alert removed.");
    }
}
