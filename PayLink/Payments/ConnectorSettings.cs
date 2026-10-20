using Dapper;
using PayLink.Infrastructure;
using PayLink.Platform;
using PayLink.Tenancy;
using WebSvc.PayLink.Abstractions;
using WebSvc.PayLink.Connector.Simulator;
using WebSvc.PayLink.Storage;

namespace PayLink.Payments;

/// <summary>
/// A tenant's payment methods: which connectors are on, separately for Test and Live, and their settings.
/// Settings are stored encrypted; secrets are never shown again. A method can only be turned on once its settings pass the connector's check.
/// </summary>
public sealed class ConnectorSettingsService(TenantDb tenantDb, ConnectorRegistry registry, SecretProtector secrets, PlatformSettings platform, ConnectorCatalog catalog)
{
    public static string ModeKey(PaymentMode mode) => mode == PaymentMode.Live ? "live" : "test";

    public async Task<IReadOnlyList<ConnectorSetting>> ListAsync(TenantRecord tenant)
    {
        await using var c = await tenantDb.OpenAsync(tenant);
        return (await c.QueryAsync<ConnectorSetting>("SELECT * FROM connector_settings ORDER BY sort_order, connector_id, mode;")).ToList();
    }

    public async Task<ConnectorSetting?> GetAsync(TenantRecord tenant, string connectorId, PaymentMode mode)
    {
        await using var c = await tenantDb.OpenAsync(tenant);
        return await c.QueryFirstOrDefaultAsync<ConnectorSetting>(
            "SELECT * FROM connector_settings WHERE connector_id = @connectorId AND mode = @mode;", new { connectorId, mode = ModeKey(mode) });
    }

    public Dictionary<string, string> Values(ConnectorSetting? setting) =>
        secrets.UnprotectJson<Dictionary<string, string>>(setting?.ConfigEnc) ?? new();

    public async Task<ConnectorConfig> ConfigAsync(TenantRecord tenant, string connectorId, PaymentMode mode, ConnectorSetting? setting = null)
    {
        setting ??= await GetAsync(tenant, connectorId, mode);
        var hook = $"{await platform.GetPublicBaseUrlAsync()}/hooks/{tenant.Slug}/{connectorId}/{ModeKey(mode)}";
        return new ConnectorConfig(mode, Values(setting), hook);
    }

    /// <summary>
    /// Saves settings (blank secret fields keep what was saved), runs the connector's check, and turns the method on or off.
    /// Turning on needs a passing check.
    /// </summary>
    public async Task<ConnectionCheck> SaveAsync(TenantRecord tenant, IPaymentConnector connector, PaymentMode mode,
        IReadOnlyDictionary<string, string?> input, bool enable, Actor actor)
    {
        if (mode == PaymentMode.Live && connector.Manifest.TestOnly)
            return new ConnectionCheck(false, $"{connector.Manifest.Name} works in Test mode only.");
        if (enable && !(await catalog.GetAsync(connector.Manifest.Id)).Allows(mode == PaymentMode.Live))
            return new ConnectionCheck(false, $"The platform hasn't allowed {connector.Manifest.Name} in {ModeKey(mode)}. Ask your platform administrator.");

        var existing = await GetAsync(tenant, connector.Manifest.Id, mode);
        var values = Values(existing);
        foreach (var field in connector.Manifest.ConfigSchema)
        {
            input.TryGetValue(field.Key, out var raw);
            var value = field.Type == ConfigFieldType.Boolean ? (raw is "true" or "on" ? "true" : "false") : raw?.Trim();
            if (field.Type == ConfigFieldType.Secret && string.IsNullOrEmpty(value)) continue;   // keep the saved secret
            if (string.IsNullOrEmpty(value)) values.Remove(field.Key); else values[field.Key] = value;
        }
        var missing = connector.Manifest.ConfigSchema.Where(f => f.Required && f.Type != ConfigFieldType.Boolean && !values.ContainsKey(f.Key)).ToList();

        var config = await ConfigAsync(tenant, connector.Manifest.Id, mode, new ConnectorSetting { ConfigEnc = secrets.ProtectJson(values) });
        ConnectionCheck check;
        if (missing.Count > 0) check = new ConnectionCheck(false, "Fill in " + string.Join(", ", missing.Select(f => f.Label)) + ".");
        else
        {
            try { check = await registry.CallAsync(connector, ct => connector.TestConnectionAsync(config, ct)); }
            catch (ConnectorCallException ex) { check = new ConnectionCheck(false, ex.Message); }
        }

        var setting = new ConnectorSetting
        {
            ConnectorId = connector.Manifest.Id, Mode = ModeKey(mode), Enabled = enable && check.Ok, ConfigEnc = secrets.ProtectJson(values),
            CheckOk = check.Ok, CheckMessage = Trim(check.Message, 500), CheckedAt = Clock.Now(), SortOrder = existing?.SortOrder ?? 0,
            UpdatedAt = Clock.Now(), UpdatedBy = actor.Name,
        };
        await using var c = await tenantDb.OpenAsync(tenant);
        await c.ExecuteAsync(Db.Dialect(tenantDb.Target(tenant).Engine).Upsert("connector_settings", ["connector_id", "mode"],
            "enabled", "config_enc", "check_ok", "check_message", "checked_at", "sort_order", "updated_at", "updated_by"),
            new
            {
                connector_id = setting.ConnectorId, mode = setting.Mode, enabled = setting.Enabled, config_enc = setting.ConfigEnc, check_ok = setting.CheckOk,
                check_message = setting.CheckMessage, checked_at = setting.CheckedAt, sort_order = setting.SortOrder, updated_at = setting.UpdatedAt, updated_by = setting.UpdatedBy,
            });
        await AuditLog.WriteAsync(c, actor, enable && check.Ok ? "method.enabled" : enable ? "method.check_failed" : "method.saved",
            $"{connector.Manifest.Id}/{ModeKey(mode)}", check.Ok ? null : check.Message);
        return check;
    }

    public async Task DisableAsync(TenantRecord tenant, string connectorId, PaymentMode mode, Actor actor)
    {
        await using var c = await tenantDb.OpenAsync(tenant);
        await c.ExecuteAsync("UPDATE connector_settings SET enabled = 0, updated_at = @at, updated_by = @by WHERE connector_id = @connectorId AND mode = @mode;",
            new { connectorId, mode = ModeKey(mode), at = Clock.Now(), by = actor.Name });
        await AuditLog.WriteAsync(c, actor, "method.disabled", $"{connectorId}/{ModeKey(mode)}");
    }

    public const string FailoverSetting = "routing_failover";
    private static readonly string GatewayError = FailureReasons.From(FailureReason.GatewayError);

    /// <summary>Gateways can stand in for each other in failover; PayLink UPI and the Simulator can't.</summary>
    public static bool IsGateway(IPaymentConnector c) => c.Manifest.Id is not (UpiConnectorId or SimulatorConnector.Id);
    private const string UpiConnectorId = "upi";
    /// <summary>The breaker opens after this many gateway errors in a row on one method, for <see cref="BreakerCooldown"/>.</summary>
    public const int BreakerErrors = 3;
    public static readonly TimeSpan BreakerCooldown = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Methods the checkout offers for a payment, in the tenant's order: turned on for its mode, able to take its currency, inside the
    /// method's amount range, and not tripped (a method whose last <see cref="BreakerErrors"/> starts were gateway errors rests for
    /// <see cref="BreakerCooldown"/>). If every method is resting, they are all offered rather than none. The Simulator is always there in Test.
    /// </summary>
    public async Task<IReadOnlyList<IPaymentConnector>> ForCheckoutAsync(TenantRecord tenant, Payment payment)
    {
        var mode = ModeKey(payment.Mode);
        var settings = (await ListAsync(tenant)).Where(s => s.Mode == mode).ToList();
        var result = new List<(int Order, IPaymentConnector Connector)>();
        foreach (var connector in registry.All)
        {
            if (!ConnectorRegistry.Supports(connector.Manifest, payment.Currency)) continue;
            if (!await catalog.OffersAsync(connector.Manifest.Id, payment.Livemode, payment.Amount)) continue;   // gate 1: the platform
            var setting = settings.FirstOrDefault(s => s.ConnectorId == connector.Manifest.Id);
            var on = connector.Manifest.Id == SimulatorConnector.Id
                ? !payment.Livemode && setting?.Enabled != false
                : setting is { Enabled: true } && !(payment.Livemode && connector.Manifest.TestOnly);
            if (!on) continue;
            if (setting?.MinAmount is { } min && payment.Amount < min) continue;
            if (setting?.MaxAmount is { } max && payment.Amount > max) continue;
            result.Add((setting?.SortOrder ?? (connector.Manifest.TestOnly ? 1000 : 0), connector));
        }
        var ordered = result.OrderBy(r => r.Order).Select(r => r.Connector).ToList();
        var tripped = await TrippedAsync(tenant, payment.Livemode);
        var healthy = ordered.Where(c => !tripped.Contains(c.Manifest.Id)).ToList();
        return healthy.Count > 0 ? healthy : ordered;
    }

    /// <summary>Methods resting after repeated gateway errors (the circuit breaker), worked out from recent attempts.</summary>
    public async Task<IReadOnlySet<string>> TrippedAsync(TenantRecord tenant, bool livemode)
    {
        await using var c = await tenantDb.OpenAsync(tenant);
        var recent = await c.QueryAsync<(string Connector, string? FailureReason, string CreatedAt)>(
            "SELECT a.connector, a.failure_reason, a.created_at FROM attempts a JOIN payments p ON p.id = a.payment_id " +
            "WHERE p.livemode = @livemode AND a.created_at >= @since ORDER BY a.created_at DESC LIMIT 500;",
            new { livemode, since = Clock.Format(DateTime.UtcNow - BreakerCooldown - TimeSpan.FromHours(1)) });
        var cutoff = Clock.Format(DateTime.UtcNow - BreakerCooldown);
        return recent.GroupBy(a => a.Connector)
            .Where(g => g.Take(BreakerErrors).Count(a => a.FailureReason == GatewayError) == BreakerErrors
                        && string.CompareOrdinal(g.First().CreatedAt, cutoff) > 0)
            .Select(g => g.Key).ToHashSet();
    }

    /// <summary>Routing for one method in one mode: its place in the checkout list and the amounts it's offered for.</summary>
    public async Task SetRoutingAsync(TenantRecord tenant, string connectorId, PaymentMode mode, int order, long? min, long? max, Actor actor)
    {
        await using var c = await tenantDb.OpenAsync(tenant);
        var n = await c.ExecuteAsync(
            "UPDATE connector_settings SET sort_order = @order, min_amount = @min, max_amount = @max, updated_at = @at, updated_by = @by WHERE connector_id = @connectorId AND mode = @mode;",
            new { connectorId, mode = ModeKey(mode), order, min, max, at = Clock.Now(), by = actor.Name });
        if (n == 0 && connectorId == SimulatorConnector.Id)   // the Simulator is on without a saved row; keep it on
            await c.ExecuteAsync(
                "INSERT INTO connector_settings (connector_id, mode, enabled, sort_order, min_amount, max_amount, updated_at, updated_by) VALUES (@connectorId, @mode, TRUE, @order, @min, @max, @at, @by);",
                new { connectorId, mode = ModeKey(mode), order, min, max, at = Clock.Now(), by = actor.Name });
        await AuditLog.WriteAsync(c, actor, "method.routing", $"{connectorId}/{ModeKey(mode)}", $"order {order}, {min?.ToString() ?? "any"} to {max?.ToString() ?? "any"}");
    }

    public async Task<bool> FailoverOnAsync(TenantRecord tenant) => await tenantDb.GetSettingAsync(tenant, FailoverSetting) != "off";

    /// <summary>Live payments need at least one live method whose settings passed the connector's check.</summary>
    public async Task<bool> IsLiveReadyAsync(TenantRecord tenant)
    {
        foreach (var s in (await ListAsync(tenant)).Where(s => s.Mode == "live" && s.Enabled && s.CheckOk == true))
            if (registry.Get(s.ConnectorId) is { Manifest.TestOnly: false } && (await catalog.GetAsync(s.ConnectorId)).Allows(true)) return true;
        return false;
    }

    private static string Trim(string value, int max) => value.Length <= max ? value : value[..max];
}
