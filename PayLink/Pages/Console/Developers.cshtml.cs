using Microsoft.AspNetCore.Mvc;
using PayLink.Payments;
using PayLink.Platform;

namespace PayLink.Pages.Console;

/// <summary>API keys, webhook endpoints and the logs a developer needs to debug an integration.</summary>
public sealed class DevelopersModel(TenantKeyService keys, WebhookService webhooks, PlatformSettings settings) : ConsolePage
{
    public IReadOnlyList<TenantApiKey> Keys { get; private set; } = [];
    public IReadOnlyList<WebhookEndpoint> Endpoints { get; private set; } = [];
    public IReadOnlyList<WebhookDelivery> Deliveries { get; private set; } = [];
    public IReadOnlyList<ApiRequestLog> Requests { get; private set; } = [];
    public string BaseUrl { get; private set; } = "";

    public async Task OnGetAsync()
    {
        Keys = await keys.ListAsync(Tenant);
        Endpoints = await webhooks.EndpointsAsync(Tenant);
        Deliveries = (await webhooks.DeliveriesAsync(Tenant, take: 100)).Where(d => d.Livemode == Livemode).Take(30).ToList();
        Requests = (await keys.RequestsAsync(Tenant, 100)).Where(r => r.Livemode is null || r.Livemode == Livemode).Take(30).ToList();
        BaseUrl = await settings.GetPublicBaseUrlAsync();
    }

    public async Task<IActionResult> OnPostKeyAsync(string name, string kind, List<string>? capabilities)
    {
        if (!CanDevelop) return NotAllowed("create keys");
        if (string.IsNullOrWhiteSpace(name)) return Failed("Name the key after where it's used, e.g. \"Shop server\".");
        if (kind == TenantKeyKinds.Developer && (capabilities is null || capabilities.Count == 0)) return Failed("Choose at least one capability for the developer key.");
        var (key, secret) = await keys.CreateAsync(Tenant, kind == TenantKeyKinds.Developer ? TenantKeyKinds.Developer : TenantKeyKinds.Secret,
            name, Livemode, capabilities, null, Me);
        TempData["Secret"] = secret;
        TempData["SecretFor"] = key.Name;
        return Redirect(Request.Path);
    }

    public async Task<IActionResult> OnPostRevokeAsync(long id)
    {
        if (!CanDevelop) return NotAllowed("revoke keys");
        await keys.RevokeAsync(Tenant, id, Me);
        return Done("Key revoked. Calls with it now get 401.");
    }

    public async Task<IActionResult> OnPostEndpointAsync(string url, string? description, List<string>? events)
    {
        if (!CanDevelop) return NotAllowed("add webhook endpoints");
        if (WebhookService.UrlProblem(url, Livemode) is { } problem) return Failed(problem);
        var (endpoint, secret) = await webhooks.AddEndpointAsync(Tenant, url, Livemode, events, description, Me);
        TempData["Secret"] = secret;
        TempData["SecretFor"] = $"webhook signing secret for {endpoint.Url}";
        return Redirect(Request.Path);
    }

    public async Task<IActionResult> OnPostRemoveEndpointAsync(long id)
    {
        if (!CanDevelop) return NotAllowed("remove webhook endpoints");
        await webhooks.RemoveEndpointAsync(Tenant, id, Me);
        return Done("Endpoint removed.");
    }

    public async Task<IActionResult> OnPostTestEndpointAsync(long id)
    {
        if (!CanDevelop) return NotAllowed("send test webhooks");
        await webhooks.SendTestAsync(Tenant, id);
        return Done("Test event queued. Its delivery shows below within a few seconds.");
    }

    public async Task<IActionResult> OnPostRedeliverAsync(long deliveryId)
    {
        if (!CanDevelop) return NotAllowed("redeliver webhooks");
        await webhooks.RedeliverAsync(Tenant, deliveryId, Me);
        return Done("Queued again.");
    }
}
