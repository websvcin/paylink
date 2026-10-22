using PayLink.Payments;

namespace PayLink.Pages.Console;

public sealed class IndexModel(PaymentService payments, ConnectorSettingsService methods, TenantKeyService keys, WebhookService webhooks, PayLink.Platform.PlatformSettings settings) : ConsolePage
{
    public int Members { get; private set; }
    public PaymentService.Summary Week { get; private set; } = new(0, 0, 0, 0, 0);
    public IReadOnlyList<Payment> Recent { get; private set; } = [];
    public bool HasMethod { get; private set; }
    public bool LiveReady { get; private set; }
    public bool HasKey { get; private set; }
    public bool HasWebhook { get; private set; }
    public string LoginLinkServer { get; private set; } = "";

    public async Task OnGetAsync()
    {
        Members = (await TenantDb.MembersAsync(Tenant)).Count(m => m.Status == "approved");
        Week = await payments.SummaryAsync(Tenant, Livemode, DateTime.UtcNow.AddDays(-7));
        Recent = await payments.SearchAsync(Tenant, Livemode, null, null, 8);
        HasMethod = (await methods.ListAsync(Tenant)).Any(s => s.Enabled && s.ConnectorId != "simulator");
        LiveReady = await methods.IsLiveReadyAsync(Tenant);
        var allKeys = await keys.ListAsync(Tenant);
        HasKey = allKeys.Any(k => k.Kind == TenantKeyKinds.Secret && k.RevokedAt is null);
        HasWebhook = (await webhooks.EndpointsAsync(Tenant)).Count > 0;
        LoginLinkServer = (await settings.GetLoginLinkAsync())?.ServerUrl ?? "";
    }
}
