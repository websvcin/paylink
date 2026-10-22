using Microsoft.AspNetCore.Mvc;
using PayLink.Payments;
using WebSvc.PayLink.Abstractions;

namespace PayLink.Pages.Console;

/// <summary>Payment methods: turn connectors on, with separate Test and Live settings. Secrets are kept and never shown again.</summary>
public sealed class MethodsModel(ConnectorRegistry registry, ConnectorSettingsService methods, PayLink.Platform.PlatformSettings settings,
    PayLink.Platform.PartnerOnboarding partners) : ConsolePage
{
    /// <summary>One plain line per method, for the cards.</summary>
    public static string Blurb(string id) => id switch
    {
        "upi" => "Free. Payers pay straight into your UPI ID from any UPI app; no gateway, no fee, no signup.",
        "simulator" => "Test payments without a gateway: the payer picks succeed, fail or pending.",
        "razorpay" => "Cards, net banking, UPI and wallets through your Razorpay account.",
        "phonepe" => "UPI, cards and net banking through PhonePe Payment Gateway.",
        "payu" => "Cards, net banking, UPI, wallets and EMI through PayU.",
        "ccavenue" => "Cards, net banking, wallets and EMI through CCAvenue.",
        "stripe" => "International cards through Stripe Checkout.",
        "paypal" => "PayPal balance and cards for international payers.",
        _ => "",
    };

    public IReadOnlyDictionary<string, PayLink.Platform.PartnerProgram> Programs { get; private set; } = new Dictionary<string, PayLink.Platform.PartnerProgram>();
    public IReadOnlyList<PayLink.Platform.PartnerRequest> Requests { get; private set; } = [];
    public PayLink.Platform.PartnerRequest? LatestRequest(string id) => Requests.FirstOrDefault(r => r.ConnectorId == id);
    public PayLink.Platform.PartnerProgram? Program(string id) => Programs.GetValueOrDefault(id) is { IsOn: true } p ? p : null;

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
        Programs = await partners.ProgramsAsync();
        Requests = await partners.ForTenantAsync(Tenant.Id);
    }

    /// <summary>"Get an account": records the tenant's interest, then opens the partner's link or files the form for the platform team.</summary>
    public async Task<IActionResult> OnPostPartnerAsync(string connector, string? name, string? email, string? phone)
    {
        if (!CanConfigure) return NotAllowed("request a payment account");
        if (registry.Get(connector) is not { } c || (await partners.ProgramAsync(connector)) is not { IsOn: true } program) return Failed("This method has no account programme.");
        if (program.Mode == PayLink.Platform.PartnerModes.Link)
        {
            await partners.RecordAsync(Tenant, connector, program.Mode, Me.Name, MyEmail, null, null, Me);
            return Redirect(program.LinkFor(Tenant.Slug)!);
        }
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(phone))
            return Failed("Add a contact name, email and phone so the team can reach you.");
        var answers = PayLink.Platform.PartnerOnboarding.FormQuestions
            .Select(q => KeyValuePair.Create(q.Label, Request.Form[$"q_{q.Key}"].ToString()))
            .Where(a => !string.IsNullOrWhiteSpace(a.Value)).ToList();
        await partners.RecordAsync(Tenant, connector, program.Mode, name, email, phone, answers, Me);
        return Done($"Request sent for {c.Manifest.Name}. The team will contact {email.Trim()}; the status shows here.");
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
