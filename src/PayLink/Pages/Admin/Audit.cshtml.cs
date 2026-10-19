using Microsoft.AspNetCore.Mvc;
using PayLink.Infrastructure;
using PayLink.Platform;

namespace PayLink.Pages.Admin;

public sealed class AuditModel(PlatformDb platformDb) : AdminPage
{
    [BindProperty(SupportsGet = true)] public int Skip { get; set; }
    public IReadOnlyList<AuditEntry> Entries { get; private set; } = [];
    public const int PageSize = 100;

    public async Task OnGetAsync()
    {
        await using var c = await platformDb.OpenAsync();
        Entries = await AuditLog.RecentAsync(c, PageSize, Math.Max(0, Skip));
    }

    // Verifying only reads, so support users may run it too.
    public async Task<IActionResult> OnGetVerifyAsync()
    {
        await using var c = await platformDb.OpenAsync();
        var broken = await AuditLog.VerifyAsync(c);
        TempData[broken is null ? "Ok" : "Error"] = broken is null
            ? "The audit chain is intact: no entry has been changed or removed."
            : $"The audit chain is broken at entry {broken}. An entry was changed or removed outside PayLink.";
        return Redirect("/admin/audit");
    }
}
