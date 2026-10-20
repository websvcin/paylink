using Dapper;
using PayLink.Infrastructure;
using PayLink.Platform;
using PayLink.Tenancy;

namespace PayLink.Payments;

public sealed class AlertRule
{
    public long Id { get; set; }
    public string Kind { get; set; } = "";
    public long Threshold { get; set; }
    public int WindowMinutes { get; set; } = 60;
    public string Channel { get; set; } = "both";
    public bool Enabled { get; set; } = true;
    public string? LastFiredAt { get; set; }
    public string CreatedAt { get; set; } = "";
    public string? CreatedBy { get; set; }
}

public sealed class AlertEvent
{
    public long Id { get; set; }
    public long RuleId { get; set; }
    public string At { get; set; } = "";
    public string Message { get; set; } = "";
    public bool Emailed { get; set; }
}

/// <summary>
/// A tenant's alerts on live payments: success rate dropping, UPI payments waiting too long, a large refund, many risk
/// blocks, gateway errors. Checked by the background job; each rule fires at most once an hour, as an alert.triggered
/// webhook and an email to the tenant's owners and admins (when the platform has email set up).
/// </summary>
public sealed class AlertService(TenantDb tenantDb, WebhookService webhooks, IEmailSender email, PlatformSettings platform)
{
    public static readonly TimeSpan Cooldown = TimeSpan.FromHours(1);

    public static readonly (string Kind, string Label, string Unit)[] Kinds =
    [
        ("success_rate_below", "Success rate below", "%"),
        ("upi_waiting_older", "A UPI payment waiting longer than", "minutes"),
        ("refund_above", "A refund above", "₹"),
        ("risk_blocks_above", "Risk blocks more than", "in the window"),
        ("gateway_errors_above", "Gateway errors more than", "in the window"),
    ];

    public async Task<IReadOnlyList<AlertRule>> RulesAsync(TenantRecord t)
    {
        await using var c = await tenantDb.OpenAsync(t);
        return (await c.QueryAsync<AlertRule>("SELECT * FROM alert_rules ORDER BY id;")).ToList();
    }

    public async Task<IReadOnlyList<AlertEvent>> RecentAsync(TenantRecord t, int take = 30)
    {
        await using var c = await tenantDb.OpenAsync(t);
        return (await c.QueryAsync<AlertEvent>($"SELECT * FROM alert_events ORDER BY id DESC LIMIT {take};")).ToList();
    }

    public async Task<string?> AddAsync(TenantRecord t, string kind, string threshold, int windowMinutes, string channel, Actor actor)
    {
        if (Kinds.All(k => k.Kind != kind)) return "Choose what to watch.";
        long value;
        if (kind == "refund_above") { if (Money.ParseMajor(threshold) is not { } m) return "Enter an amount in rupees."; value = m; }
        else if (!long.TryParse(threshold, out value) || value <= 0 || (kind == "success_rate_below" && value > 100)) return "Enter a positive whole number" + (kind == "success_rate_below" ? " up to 100." : ".");
        if (windowMinutes is < 5 or > 1440) return "The window is between 5 minutes and a day.";
        if (channel is not ("both" or "webhook" or "email")) channel = "both";
        await using var c = await tenantDb.OpenAsync(t);
        await c.ExecuteAsync("INSERT INTO alert_rules (kind, threshold, window_minutes, channel, enabled, created_at, created_by) VALUES (@kind, @value, @windowMinutes, @channel, TRUE, @at, @by);",
            new { kind, value, windowMinutes, channel, at = Clock.Now(), by = actor.Name });
        await AuditLog.WriteAsync(c, actor, "alert.rule_added", kind, $"{threshold} / {windowMinutes} min / {channel}");
        return null;
    }

    public async Task RemoveAsync(TenantRecord t, long id, Actor actor)
    {
        await using var c = await tenantDb.OpenAsync(t);
        await c.ExecuteAsync("DELETE FROM alert_rules WHERE id = @id;", new { id });
        await AuditLog.WriteAsync(c, actor, "alert.rule_removed", id.ToString());
    }

    /// <summary>Checks every rule; returns how many fired.</summary>
    public async Task<int> EvaluateAsync(TenantRecord t, CancellationToken ct = default)
    {
        await using var c = await tenantDb.OpenAsync(t);
        var rules = (await c.QueryAsync<AlertRule>("SELECT * FROM alert_rules WHERE enabled = TRUE;")).ToList();
        var fired = 0;
        foreach (var rule in rules)
        {
            if (Clock.Parse(rule.LastFiredAt) is { } last && DateTime.UtcNow - last < Cooldown) continue;
            var since = Clock.Format(DateTime.UtcNow.AddMinutes(-rule.WindowMinutes));
            string? message = null;
            switch (rule.Kind)
            {
                case "success_rate_below":
                    var stats = (await c.QueryAsync<string>(
                        "SELECT a.status FROM attempts a JOIN payments p ON p.id = a.payment_id WHERE p.livemode = TRUE AND a.updated_at >= @since AND a.status IN ('succeeded', 'failed', 'cancelled', 'expired');",
                        new { since })).ToList();
                    if (stats.Count >= 10)
                    {
                        var rate = 100.0 * stats.Count(s => s == "succeeded") / stats.Count;
                        if (rate < rule.Threshold) message = $"Only {rate:0}% of the last {stats.Count} payment attempts succeeded in {rule.WindowMinutes} minutes (alert below {rule.Threshold}%).";
                    }
                    break;
                case "upi_waiting_older":
                    var oldest = await c.ExecuteScalarAsync<string?>(
                        "SELECT MIN(a.updated_at) FROM attempts a JOIN payments p ON p.id = a.payment_id WHERE a.connector = 'upi' AND a.status = 'processing' AND p.livemode = TRUE;");
                    if (Clock.Parse(oldest) is { } o && DateTime.UtcNow - o > TimeSpan.FromMinutes(rule.Threshold))
                        message = $"A UPI payment has waited {(int)(DateTime.UtcNow - o).TotalMinutes} minutes for verification (alert after {rule.Threshold}).";
                    break;
                case "refund_above":
                    var big = await c.ExecuteScalarAsync<long?>("SELECT MAX(amount) FROM refunds WHERE livemode = TRUE AND created_at >= @since AND amount > @t;", new { since, t = rule.Threshold });
                    if (big is { } b) message = $"A refund of {Money.Format(b, "INR")} was made (alert above {Money.Format(rule.Threshold, "INR")}).";
                    break;
                case "risk_blocks_above":
                    var blocks = await c.ExecuteScalarAsync<long>(
                        "SELECT COUNT(*) FROM payment_events e JOIN payments p ON p.id = e.payment_id WHERE e.type = 'risk.blocked' AND p.livemode = TRUE AND e.at >= @since;", new { since });
                    if (blocks > rule.Threshold) message = $"Risk rules blocked {blocks} payments in {rule.WindowMinutes} minutes (alert above {rule.Threshold}).";
                    break;
                case "gateway_errors_above":
                    var errors = await c.ExecuteScalarAsync<long>(
                        "SELECT COUNT(*) FROM attempts a JOIN payments p ON p.id = a.payment_id WHERE a.failure_reason = 'gateway_error' AND p.livemode = TRUE AND a.updated_at >= @since;", new { since });
                    if (errors > rule.Threshold) message = $"{errors} payment attempts failed with gateway errors in {rule.WindowMinutes} minutes (alert above {rule.Threshold}).";
                    break;
            }
            if (message is null) continue;

            var emailed = false;
            if (rule.Channel is "both" or "email")
            {
                var to = (await tenantDb.MembersAsync(t)).Where(m => m.Status == "approved" && m.Role is TenantRoles.Owner or TenantRoles.Admin && m.Email is not null)
                    .Select(m => m.Email!).ToList();
                emailed = await email.SendAsync(to, $"PayLink alert for {t.Name}", message + $"\n\n{await platform.GetPublicBaseUrlAsync()}/{t.Slug}/console", ct);
            }
            await c.ExecuteAsync("INSERT INTO alert_events (rule_id, at, message, emailed) VALUES (@Id, @at, @message, @emailed);", new { rule.Id, at = Clock.Now(), message, emailed });
            await c.ExecuteAsync("UPDATE alert_rules SET last_fired_at = @at WHERE id = @Id;", new { rule.Id, at = Clock.Now() });
            if (rule.Channel is "both" or "webhook")
                await webhooks.EnqueueAsync(c, WebhookEvents.AlertTriggered, true, null, new { kind = rule.Kind, threshold = rule.Threshold, window_minutes = rule.WindowMinutes, message });
            fired++;
        }
        return fired;
    }
}
