using System.Collections.Concurrent;
using System.Data.Common;
using Dapper;
using PayLink.Infrastructure;
using PayLink.Platform;
using WebSvc.PayLink.Storage;

namespace PayLink.Tenancy;

public sealed class Member
{
    public long Id { get; set; }
    public string Subject { get; set; } = "";
    public string? Email { get; set; }
    public string? Name { get; set; }
    public string Role { get; set; } = TenantRoles.Viewer;
    public string Status { get; set; } = "";
    public string Source { get; set; } = "";
    public string CreatedAt { get; set; } = "";
    public string? DecidedBy { get; set; }
    public string? DecidedAt { get; set; }
    public string? LastSignInAt { get; set; }
}

/// <summary>Opens a tenant's own database (SQLite or MySQL) from the encrypted connection in the tenant registry.</summary>
public sealed class TenantDb(SecretProtector secrets, AppPaths paths, ILogger<TenantDb> logger)
{
    private readonly ConcurrentDictionary<long, DatabaseTarget> _targets = new();

    public DatabaseTarget Target(TenantRecord tenant) =>
        _targets.GetOrAdd(tenant.Id, _ => paths.Resolve(secrets.UnprotectJson<DatabaseTarget>(tenant.DbConnectionEnc)
            ?? throw new InvalidOperationException($"Tenant {tenant.Slug} has no database connection.")));

    public async Task<DbConnection> OpenAsync(TenantRecord tenant) => await Db.OpenAsync(Target(tenant));

    /// <summary>Forgets cached connections, e.g. after a restore or after a tenant's database moved.</summary>
    public void ClearCache(long? tenantId = null)
    {
        if (tenantId is { } id) _targets.TryRemove(id, out _); else _targets.Clear();
    }

    public async Task<MigrationResult> MigrateAsync(DatabaseTarget target, string label)
    {
        var scripts = SqlMigrator.LoadFromDirectory(paths.Scripts(PlatformDb.EngineFolder(target.Engine), "tenant"));
        var result = await SqlMigrator.ApplyAsync(target, scripts);
        if (result.Applied.Count > 0)
            logger.LogInformation("Tenant {Tenant}: applied migrations {Versions}", label, string.Join(", ", result.Applied));
        return result;
    }

    public async Task AuditAsync(TenantRecord tenant, Actor actor, string action, string? target, string? details = null)
    {
        await using var c = await OpenAsync(tenant);
        await AuditLog.WriteAsync(c, actor, action, target, details);
    }

    // ---------- settings ----------

    public async Task<string?> GetSettingAsync(TenantRecord tenant, string key)
    {
        await using var c = await OpenAsync(tenant);
        return await c.ExecuteScalarAsync<string?>("SELECT svalue FROM tenant_settings WHERE skey = @key;", new { key });
    }

    public async Task SetSettingAsync(TenantRecord tenant, string key, string value)
    {
        await using var c = await OpenAsync(tenant);
        var dialect = Db.Dialect(Target(tenant).Engine);
        await c.ExecuteAsync(dialect.Upsert("tenant_settings", "skey", "svalue", "updated_at"), new { skey = key, svalue = value, updated_at = Clock.Now() });
    }

    public async Task<string> GetAdmissionModeAsync(TenantRecord tenant) =>
        await GetSettingAsync(tenant, "admission_mode") ?? AdmissionModes.AutoTenantAdmins;

    // ---------- members ----------

    public async Task<Member?> MemberBySubjectAsync(TenantRecord tenant, string subject)
    {
        await using var c = await OpenAsync(tenant);
        return await c.QueryFirstOrDefaultAsync<Member>("SELECT * FROM members WHERE subject = @subject;", new { subject });
    }

    public async Task<Member?> MemberByIdAsync(TenantRecord tenant, long id)
    {
        await using var c = await OpenAsync(tenant);
        return await c.QueryFirstOrDefaultAsync<Member>("SELECT * FROM members WHERE id = @id;", new { id });
    }

    public async Task<IReadOnlyList<Member>> MembersAsync(TenantRecord tenant)
    {
        await using var c = await OpenAsync(tenant);
        return (await c.QueryAsync<Member>("SELECT * FROM members WHERE status <> 'removed' ORDER BY status DESC, id;")).ToList();
    }

    public async Task UpsertMemberAsync(TenantRecord tenant, Member m)
    {
        await using var c = await OpenAsync(tenant);
        var existing = await c.ExecuteScalarAsync<long?>("SELECT id FROM members WHERE subject = @Subject;", m);
        if (existing is null)
            await c.ExecuteAsync(
                "INSERT INTO members (subject, email, name, role, status, source, created_at, decided_by, decided_at, last_sign_in_at) " +
                "VALUES (@Subject, @Email, @Name, @Role, @Status, @Source, @CreatedAt, @DecidedBy, @DecidedAt, @LastSignInAt);", m);
        else
            await c.ExecuteAsync(
                "UPDATE members SET email = @Email, name = @Name, role = @Role, status = @Status, source = @Source, " +
                "decided_by = @DecidedBy, decided_at = @DecidedAt, last_sign_in_at = @LastSignInAt WHERE subject = @Subject;", m);
    }

    public async Task<int> CountOwnersAsync(TenantRecord tenant)
    {
        await using var c = await OpenAsync(tenant);
        return await c.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM members WHERE role = 'owner' AND status = 'approved';");
    }

    // ---------- role map ----------

    public async Task<IReadOnlyList<(string LoginLinkRole, string PayLinkRole)>> RoleMapAsync(TenantRecord tenant)
    {
        await using var c = await OpenAsync(tenant);
        return (await c.QueryAsync<(string, string)>("SELECT loginlink_role, paylink_role FROM role_map ORDER BY loginlink_role;")).ToList();
    }

    public async Task SetRoleMapAsync(TenantRecord tenant, string loginLinkRole, string? payLinkRole)
    {
        await using var c = await OpenAsync(tenant);
        if (payLinkRole is null)
        {
            await c.ExecuteAsync("DELETE FROM role_map WHERE loginlink_role = @loginLinkRole;", new { loginLinkRole });
            return;
        }
        var dialect = Db.Dialect(Target(tenant).Engine);
        await c.ExecuteAsync(dialect.Upsert("role_map", "loginlink_role", "paylink_role"), new { loginlink_role = loginLinkRole, paylink_role = payLinkRole });
    }
}
