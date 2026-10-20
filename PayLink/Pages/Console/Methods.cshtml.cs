using Microsoft.AspNetCore.Mvc;
using PayLink.Payments;
using WebSvc.PayLink.Abstractions;

namespace PayLink.Pages.Console;

/// <summary>Payment methods: turn connectors on, with separate Test and Live settings. Secrets are kept and never shown again.</summary>
public sealed class MethodsModel(ConnectorRegistry registry, ConnectorSettingsService methods, PayLink.Platform.PlatformSettings settings) : ConsolePage
{
    public IReadOnlyList<IPaymentConnector> Connectors => registry.All;
    public IReadOnlyList<ConnectorSetting> Saved { get; private set; } = [];
    public bool LiveReady { get; private set; }

    public ConnectorSetting? Setting(string id, string mode) => Saved.FirstOrDefault(s => s.ConnectorId == id && s.Mode == mode);
    public Dictionary<string, string> Values(ConnectorSetting? s) => methods.Values(s);

    public string BaseUrl { get; private set; } = "";
    public string HookUrl(string connector, string mode) => $"{BaseUrl}/hooks/{Tenant.Slug}/{connector}/{mode}";

    /// <summary>Routing rows for the console's current mode: the methods that are on, in checkout order.</summary>
    public IReadOnlyList<(IPaymentConnector Connector, ConnectorSetting? Setting)> Routing { get; private set; } = [];
    public IReadOnlySet<string> Tripped { get; private set; } = new HashSet<string>();
    public bool Failover { get; private set; }

    public async Task OnGetAsync()
    {
        BaseUrl = await settings.GetPublicBaseUrlAsync();
        Saved = await methods.ListAsync(Tenant);
        LiveReady = await methods.IsLiveReadyAsync(Tenant);
        Routing = Connectors
            .Select(c => (Connector: c, Setting: Setting(c.Manifest.Id, ModeName)))
            .Where(r => r.Connector.Manifest.Id == WebSvc.PayLink.Connector.Simulator.SimulatorConnector.Id
                ? !Livemode && r.Setting?.Enabled != false
                : r.Setting is { Enabled: true })
            .OrderBy(r => r.Setting?.SortOrder ?? (r.Connector.Manifest.TestOnly ? 1000 : 0)).ToList();
        Tripped = await methods.TrippedAsync(Tenant, Livemode);
        Failover = await methods.FailoverOnAsync(Tenant);
    }

    public async Task<IActionResult> OnPostRoutingAsync()
    {
        if (!CanConfigure) return NotAllowed("change payment methods");
        var mode = Livemode ? PaymentMode.Live : PaymentMode.Test;
        foreach (var c in Connectors)
        {
            var id = c.Manifest.Id;
            if (!Request.Form.ContainsKey($"order_{id}")) continue;
            var min = Money.ParseMajor(Request.Form[$"min_{id}"]);
            var max = Money.ParseMajor(Request.Form[$"max_{id}"]);
            if (min is not null && max is not null && min > max) return Failed($"{c.Manifest.Name}: the lowest amount is more than the highest.");
            await methods.SetRoutingAsync(Tenant, id, mode, int.TryParse(Request.Form[$"order_{id}"], out var order) ? Math.Clamp(order, 0, 999) : 0, min, max, Me);
        }
        if (Request.Form.ContainsKey("failover"))
        {
            var on = Request.Form["failover"].Contains("on");
            await TenantDb.SetSettingAsync(Tenant, ConnectorSettingsService.FailoverSetting, on ? "on" : "off");
        }
        return Done($"Routing saved for {ModeName}.");
    }

    public async Task<IActionResult> OnPostAsync(string connector, string mode, string action)
    {
        if (!CanConfigure) return NotAllowed("change payment methods");
        if (registry.Get(connector) is not { } c || mode is not ("test" or "live")) return Failed("Unknown payment method.");
        var paymentMode = mode == "live" ? PaymentMode.Live : PaymentMode.Test;
        if (action == "disable")
        {
            await methods.DisableAsync(Tenant, connector, paymentMode, Me);
            return Done($"{c.Manifest.Name} is off in {mode}.");
        }
        var input = c.Manifest.ConfigSchema.ToDictionary(f => f.Key, f => (string?)Request.Form[$"{mode}.{f.Key}"].ToString());
        var check = await methods.SaveAsync(Tenant, c, paymentMode, input, enable: action == "enable", Me);
        if (!check.Ok) return Failed($"{c.Manifest.Name} ({mode}): {check.Message}");
        return Done(action == "enable" ? $"{c.Manifest.Name} is on in {mode}. {check.Message}" : $"Saved. {check.Message}");
    }
}
