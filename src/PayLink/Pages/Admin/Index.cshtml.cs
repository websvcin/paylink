using PayLink.Infrastructure;
using PayLink.Platform;
using WebSvc.PayLink.Storage;
using WebSvc.PayLink.Storage.Provisioning;

namespace PayLink.Pages.Admin;

public sealed class IndexModel(PlatformRepository repo, PlatformSettings settings, PlatformDb platformDb, BootstrapStore bootstrap, HealthState health) : AdminPage
{
    public (long Active, long Suspended, long Admins, long PendingAdmins, long Approvals, long Keys) Counts { get; private set; }
    public LoginLinkSettings? LoginLink { get; private set; }
    public HostingSettings Hosting { get; private set; } = new();
    public DatabaseTarget? PlatformTarget { get; private set; }
    public bool SignupOpen { get; private set; }
    public IReadOnlyList<AuditEntry> Recent { get; private set; } = [];
    public IReadOnlyCollection<string> Degraded => health.DegradedTenants;

    public async Task OnGetAsync()
    {
        Counts = await repo.CountsAsync();
        LoginLink = await settings.GetLoginLinkAsync();
        Hosting = await bootstrap.GetHostingAsync();
        PlatformTarget = await bootstrap.GetPlatformDatabaseAsync();
        SignupOpen = await settings.IsSignupOpenAsync();
        await using var c = await platformDb.OpenAsync();
        Recent = await AuditLog.RecentAsync(c, 8);
    }
}
