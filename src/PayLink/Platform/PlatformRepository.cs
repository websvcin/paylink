using System.Security.Cryptography;
using System.Text;
using Dapper;
using PayLink.Infrastructure;

namespace PayLink.Platform;

/// <summary>Queries against the platform database. Every method opens and closes its own connection.</summary>
public sealed class PlatformRepository(PlatformDb db)
{
    // ---------- tenants ----------

    public async Task<TenantRecord?> TenantBySlugAsync(string slug)
    {
        await using var c = await db.OpenAsync();
        return await c.QueryFirstOrDefaultAsync<TenantRecord>("SELECT * FROM tenants WHERE slug = @slug;", new { slug });
    }

    public async Task<TenantRecord?> TenantByIdAsync(long id)
    {
        await using var c = await db.OpenAsync();
        return await c.QueryFirstOrDefaultAsync<TenantRecord>("SELECT * FROM tenants WHERE id = @id;", new { id });
    }

    public async Task<bool> OrgInUseAsync(string orgSlug)
    {
        await using var c = await db.OpenAsync();
        return await c.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM tenants WHERE loginlink_org = @orgSlug;", new { orgSlug }) > 0;
    }

    public async Task<IReadOnlyList<TenantRecord>> TenantsAsync(string? search = null, long? createdByKeyId = null, int take = 200)
    {
        await using var c = await db.OpenAsync();
        var sql = "SELECT * FROM tenants WHERE 1=1";
        if (!string.IsNullOrWhiteSpace(search)) sql += " AND (slug LIKE @q OR name LIKE @q OR loginlink_org LIKE @q)";
        if (createdByKeyId is not null) sql += " AND created_by_key_id = @createdByKeyId";
        sql += " ORDER BY id DESC LIMIT @take;";
        return (await c.QueryAsync<TenantRecord>(sql, new { q = $"%{search}%", createdByKeyId, take })).ToList();
    }

    public async Task<long> InsertTenantAsync(TenantRecord t)
    {
        await using var c = await db.OpenAsync();
        var dialect = await db.DialectAsync();
        await c.ExecuteAsync(
            "INSERT INTO tenants (slug, name, status, db_mode, db_engine, db_display, db_connection_enc, db_provider_ref_enc, " +
            "loginlink_org, loginlink_issuer, loginlink_mode, console_app_id, console_client_id, console_client_secret_enc, " +
            "mgmt_client_id, mgmt_client_secret_enc, api_registration, api_registration_error, created_at, created_by, created_via, created_by_key_id) " +
            "VALUES (@Slug, @Name, @Status, @DbMode, @DbEngine, @DbDisplay, @DbConnectionEnc, @DbProviderRefEnc, " +
            "@LoginlinkOrg, @LoginlinkIssuer, @LoginlinkMode, @ConsoleAppId, @ConsoleClientId, @ConsoleClientSecretEnc, " +
            "@MgmtClientId, @MgmtClientSecretEnc, @ApiRegistration, @ApiRegistrationError, @CreatedAt, @CreatedBy, @CreatedVia, @CreatedByKeyId);", t);
        return await c.ExecuteScalarAsync<long>(dialect.LastInsertId);
    }

    public async Task SetTenantStatusAsync(long id, string status)
    {
        await using var c = await db.OpenAsync();
        await c.ExecuteAsync("UPDATE tenants SET status = @status WHERE id = @id;", new { id, status });
    }

    public async Task<long> CountTenantsCreatedByKeyAsync(long keyId)
    {
        await using var c = await db.OpenAsync();
        return await c.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM tenants WHERE created_by_key_id = @keyId;", new { keyId });
    }

    // ---------- platform admins ----------

    public async Task<PlatformAdmin?> AdminBySubjectAsync(string subject)
    {
        await using var c = await db.OpenAsync();
        return await c.QueryFirstOrDefaultAsync<PlatformAdmin>("SELECT * FROM platform_admins WHERE subject = @subject;", new { subject });
    }

    public async Task<IReadOnlyList<PlatformAdmin>> AdminsAsync()
    {
        await using var c = await db.OpenAsync();
        return (await c.QueryAsync<PlatformAdmin>("SELECT * FROM platform_admins WHERE status <> 'removed' ORDER BY status DESC, id;")).ToList();
    }

    public async Task UpsertAdminAsync(PlatformAdmin a)
    {
        await using var c = await db.OpenAsync();
        var existing = await c.ExecuteScalarAsync<long?>("SELECT id FROM platform_admins WHERE subject = @Subject;", a);
        if (existing is null)
            await c.ExecuteAsync(
                "INSERT INTO platform_admins (subject, email, name, access, status, source, created_at, decided_by, decided_at, last_sign_in_at) " +
                "VALUES (@Subject, @Email, @Name, @Access, @Status, @Source, @CreatedAt, @DecidedBy, @DecidedAt, @LastSignInAt);", a);
        else
            await c.ExecuteAsync(
                "UPDATE platform_admins SET email = @Email, name = @Name, access = @Access, status = @Status, source = @Source, " +
                "decided_by = @DecidedBy, decided_at = @DecidedAt, last_sign_in_at = @LastSignInAt WHERE subject = @Subject;", a);
    }

    public async Task<int> CountPendingAdminsAsync()
    {
        await using var c = await db.OpenAsync();
        return await c.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM platform_admins WHERE status = 'pending';");
    }

    // ---------- auto-provisioning approvals ----------

    public async Task<IReadOnlyList<AutoProvisionApproval>> ApprovalsAsync()
    {
        await using var c = await db.OpenAsync();
        return (await c.QueryAsync<AutoProvisionApproval>("SELECT * FROM autoprov_approvals ORDER BY org_slug;")).ToList();
    }

    public async Task<bool> IsApprovedAsync(string orgSlug)
    {
        await using var c = await db.OpenAsync();
        return await c.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM autoprov_approvals WHERE org_slug = @orgSlug;", new { orgSlug }) > 0;
    }

    /// <summary>Returns false when the slug was already approved.</summary>
    public async Task<bool> AddApprovalAsync(string orgSlug, string? note, string? by)
    {
        await using var c = await db.OpenAsync();
        if (await c.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM autoprov_approvals WHERE org_slug = @orgSlug;", new { orgSlug }) > 0) return false;
        await c.ExecuteAsync("INSERT INTO autoprov_approvals (org_slug, note, created_at, created_by) VALUES (@orgSlug, @note, @at, @by);",
            new { orgSlug, note, at = Clock.Now(), by });
        return true;
    }

    public async Task<bool> RemoveApprovalAsync(string orgSlug)
    {
        await using var c = await db.OpenAsync();
        return await c.ExecuteAsync("DELETE FROM autoprov_approvals WHERE org_slug = @orgSlug;", new { orgSlug }) > 0;
    }

    public async Task MarkApprovalUsedAsync(string orgSlug)
    {
        await using var c = await db.OpenAsync();
        await c.ExecuteAsync("UPDATE autoprov_approvals SET used_at = @at WHERE org_slug = @orgSlug;", new { orgSlug, at = Clock.Now() });
    }

    // ---------- developer keys ----------

    public static string HashKey(string key) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))).ToLowerInvariant();

    /// <summary>Creates a key and returns its full value. The value is never stored, only its hash.</summary>
    public async Task<(DeveloperKey Key, string Secret)> CreateDeveloperKeyAsync(
        string name, IEnumerable<string> capabilities, string reach, int? creationLimit, string? expiresAt, string? createdBy)
    {
        var secret = "pdk_" + WebSvc.PayLink.Storage.Provisioning.ProvisioningNames.RandomToken(40, includeUpper: true);
        var key = new DeveloperKey
        {
            Name = name, KeyPrefix = secret[..8], KeyHash = HashKey(secret), Capabilities = string.Join(",", capabilities),
            Reach = reach, CreationLimit = creationLimit, ExpiresAt = expiresAt, CreatedAt = Clock.Now(), CreatedBy = createdBy,
        };
        await using var c = await db.OpenAsync();
        var dialect = await db.DialectAsync();
        await c.ExecuteAsync(
            "INSERT INTO developer_keys (name, key_prefix, key_hash, capabilities, reach, tenant_ids, creation_limit, expires_at, created_at, created_by) " +
            "VALUES (@Name, @KeyPrefix, @KeyHash, @Capabilities, @Reach, @TenantIds, @CreationLimit, @ExpiresAt, @CreatedAt, @CreatedBy);", key);
        key.Id = await c.ExecuteScalarAsync<long>(dialect.LastInsertId);
        return (key, secret);
    }

    public async Task<DeveloperKey?> DeveloperKeyBySecretAsync(string secret)
    {
        await using var c = await db.OpenAsync();
        return await c.QueryFirstOrDefaultAsync<DeveloperKey>("SELECT * FROM developer_keys WHERE key_hash = @hash;", new { hash = HashKey(secret) });
    }

    public async Task<IReadOnlyList<DeveloperKey>> DeveloperKeysAsync()
    {
        await using var c = await db.OpenAsync();
        return (await c.QueryAsync<DeveloperKey>("SELECT * FROM developer_keys ORDER BY revoked_at IS NOT NULL, id DESC;")).ToList();
    }

    public async Task RevokeDeveloperKeyAsync(long id)
    {
        await using var c = await db.OpenAsync();
        await c.ExecuteAsync("UPDATE developer_keys SET revoked_at = @at WHERE id = @id AND revoked_at IS NULL;", new { id, at = Clock.Now() });
    }

    public async Task TouchDeveloperKeyAsync(long id)
    {
        await using var c = await db.OpenAsync();
        await c.ExecuteAsync("UPDATE developer_keys SET last_used_at = @at WHERE id = @id;", new { id, at = Clock.Now() });
    }

    // ---------- signup sessions ----------

    public async Task SaveSignupSessionAsync(string id, string payloadEnc, TimeSpan lifetime)
    {
        await using var c = await db.OpenAsync();
        var dialect = await db.DialectAsync();
        await c.ExecuteAsync("DELETE FROM signup_sessions WHERE expires_at < @now;", new { now = Clock.Now() });
        await c.ExecuteAsync(dialect.Upsert("signup_sessions", "id", "payload_enc", "created_at", "expires_at"),
            new { id, payload_enc = payloadEnc, created_at = Clock.Now(), expires_at = Clock.Format(DateTime.UtcNow + lifetime) });
    }

    public async Task<string?> LoadSignupSessionAsync(string id)
    {
        await using var c = await db.OpenAsync();
        return await c.ExecuteScalarAsync<string?>("SELECT payload_enc FROM signup_sessions WHERE id = @id AND expires_at >= @now;", new { id, now = Clock.Now() });
    }

    public async Task DeleteSignupSessionAsync(string id)
    {
        await using var c = await db.OpenAsync();
        await c.ExecuteAsync("DELETE FROM signup_sessions WHERE id = @id;", new { id });
    }

    // ---------- dashboard ----------

    public async Task<(long Active, long Suspended, long Admins, long PendingAdmins, long Approvals, long Keys)> CountsAsync()
    {
        await using var c = await db.OpenAsync();
        return (
            await c.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM tenants WHERE status = 'active';"),
            await c.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM tenants WHERE status = 'suspended';"),
            await c.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM platform_admins WHERE status = 'approved';"),
            await c.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM platform_admins WHERE status = 'pending';"),
            await c.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM autoprov_approvals;"),
            await c.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM developer_keys WHERE revoked_at IS NULL;"));
    }
}
