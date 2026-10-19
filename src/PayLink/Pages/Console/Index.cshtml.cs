namespace PayLink.Pages.Console;

public sealed class IndexModel : ConsolePage
{
    public int Members { get; private set; }

    public async Task OnGetAsync() => Members = (await TenantDb.MembersAsync(Tenant)).Count(m => m.Status == "approved");
}
