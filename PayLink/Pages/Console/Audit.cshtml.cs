using Microsoft.AspNetCore.Mvc;
using PayLink.Infrastructure;

namespace PayLink.Pages.Console;

public sealed class AuditModel : ConsolePage
{
    public IReadOnlyList<AuditEntry> Entries { get; private set; } = [];
    public string? ChainStatus { get; private set; }

    public async Task OnGetAsync(bool verify = false)
    {
        await using var c = await TenantDb.OpenAsync(Tenant);
        Entries = await AuditLog.RecentAsync(c, 200);
        if (verify)
        {
            var broken = await AuditLog.VerifyAsync(c);
            ChainStatus = broken is null ? "The audit chain is intact." : $"The audit chain is broken at entry {broken}.";
        }
    }
}
