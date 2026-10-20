using Dapper;
using PayLink.Infrastructure;
using PayLink.Platform;
using PayLink.Tenancy;

namespace PayLink.Payments;

public sealed class RiskRuleStore(TenantDb tenantDb)
{
    public async Task<IReadOnlyList<RiskRule>> ListAsync(TenantRecord tenant)
    {
        await using var c = await tenantDb.OpenAsync(tenant);
        return (await c.QueryAsync<RiskRule>("SELECT * FROM risk_rules ORDER BY id;")).ToList();
    }

    /// <summary>Returns a problem, or null when the rule was added.</summary>
    public async Task<string?> AddAsync(TenantRecord tenant, string kind, string value, string action, string? note, Actor actor)
    {
        value = value?.Trim() ?? "";
        if (RiskEngine.Problem(kind, value, action) is { } problem) return problem;
        await using var c = await tenantDb.OpenAsync(tenant);
        await c.ExecuteAsync("INSERT INTO risk_rules (kind, value, action, enabled, note, created_at, created_by) VALUES (@kind, @value, @action, TRUE, @note, @at, @by);",
            new { kind, value, action, note = string.IsNullOrWhiteSpace(note) ? null : note.Trim(), at = Clock.Now(), by = actor.Name });
        await AuditLog.WriteAsync(c, actor, "risk.rule_added", $"{kind} = {value}", action);
        return null;
    }

    public async Task<bool> RemoveAsync(TenantRecord tenant, long id, Actor actor)
    {
        await using var c = await tenantDb.OpenAsync(tenant);
        var rule = await c.QueryFirstOrDefaultAsync<RiskRule>("SELECT * FROM risk_rules WHERE id = @id;", new { id });
        if (rule is null) return false;
        await c.ExecuteAsync("DELETE FROM risk_rules WHERE id = @id;", new { id });
        await AuditLog.WriteAsync(c, actor, "risk.rule_removed", $"{rule.Kind} = {rule.Value}", rule.Action);
        return true;
    }
}
