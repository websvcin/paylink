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

    public async Task OnGetAsync()
    {
        BaseUrl = await settings.GetPublicBaseUrlAsync();
        Saved = await methods.ListAsync(Tenant);
        LiveReady = await methods.IsLiveReadyAsync(Tenant);
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
