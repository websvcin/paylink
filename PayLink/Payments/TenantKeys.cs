using System.Security.Cryptography;
using System.Text;
using Dapper;
using PayLink.Infrastructure;
using PayLink.Platform;
using PayLink.Tenancy;

namespace PayLink.Payments;

/// <summary>
/// A tenant's API keys. Secret keys (sk_test_…, sk_live_…) take payments; tenant developer keys (tdk_…) automate the console
/// within their capabilities. A key names its tenant (sk_test_acme_…) so PayLink knows which database to check; only a hash is stored.
/// Keys are created and revoked only from a signed-in console session, so a leaked key can't mint another.
/// </summary>
public sealed class TenantKeyService(TenantDb tenantDb)
{
    public static string Hash(string key) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))).ToLowerInvariant();

    /// <summary>Splits "sk_test_acme_XXXX" / "tdk_acme_XXXX" into kind, mode and tenant slug. Null when it isn't a PayLink tenant key.</summary>
    public static (string Kind, bool Livemode, string Slug)? Parse(string key)
    {
        string rest;
        string kind;
        bool live = false;
        if (key.StartsWith("sk_test_", StringComparison.Ordinal)) { kind = TenantKeyKinds.Secret; rest = key[8..]; }
        else if (key.StartsWith("sk_live_", StringComparison.Ordinal)) { kind = TenantKeyKinds.Secret; live = true; rest = key[8..]; }
        else if (key.StartsWith("tdk_", StringComparison.Ordinal)) { kind = TenantKeyKinds.Developer; rest = key[4..]; }
        else return null;
        var cut = rest.LastIndexOf('_');
        return cut <= 0 ? null : (kind, live, rest[..cut]);
    }

    public async Task<IReadOnlyList<TenantApiKey>> ListAsync(TenantRecord tenant)
    {
        await using var c = await tenantDb.OpenAsync(tenant);
        return (await c.QueryAsync<TenantApiKey>("SELECT * FROM api_keys ORDER BY revoked_at IS NOT NULL, id DESC;")).ToList();
    }

    public async Task<(TenantApiKey Key, string Secret)> CreateAsync(TenantRecord tenant, string kind, string name, bool livemode,
        IEnumerable<string>? capabilities, string? expiresAt, Actor actor)
    {
        var token = WebSvc.PayLink.Storage.Provisioning.ProvisioningNames.RandomToken(32, includeUpper: true);
        var secret = kind == TenantKeyKinds.Secret ? $"sk_{(livemode ? "live" : "test")}_{tenant.Slug}_{token}" : $"tdk_{tenant.Slug}_{token}";
        var key = new TenantApiKey
        {
            Name = name.Trim(), Kind = kind, Livemode = kind == TenantKeyKinds.Secret && livemode,
            KeyPrefix = secret[..(secret.Length - token.Length + 4)] + "…", KeyHash = Hash(secret),
            Capabilities = kind == TenantKeyKinds.Developer ? string.Join(",", (capabilities ?? []).Where(c => TenantCapabilities.All.Any(a => a.Key == c))) : null,
            CreatedAt = Clock.Now(), CreatedBy = actor.Name, ExpiresAt = expiresAt,
        };
        await using var c = await tenantDb.OpenAsync(tenant);
        await c.ExecuteAsync(
            "INSERT INTO api_keys (name, kind, livemode, key_prefix, key_hash, capabilities, created_at, created_by, expires_at) " +
            "VALUES (@Name, @Kind, @Livemode, @KeyPrefix, @KeyHash, @Capabilities, @CreatedAt, @CreatedBy, @ExpiresAt);", key);
        key.Id = await c.ExecuteScalarAsync<long>(WebSvc.PayLink.Storage.Db.Dialect(tenantDb.Target(tenant).Engine).LastInsertId);
        await AuditLog.WriteAsync(c, actor, "key.created", key.KeyPrefix, $"{key.Name} · {(kind == TenantKeyKinds.Secret ? (livemode ? "live secret" : "test secret") : "developer: " + key.Capabilities)}");
        return (key, secret);
    }

    public async Task<bool> RevokeAsync(TenantRecord tenant, long id, Actor actor)
    {
        await using var c = await tenantDb.OpenAsync(tenant);
        var key = await c.QueryFirstOrDefaultAsync<TenantApiKey>("SELECT * FROM api_keys WHERE id = @id AND revoked_at IS NULL;", new { id });
        if (key is null) return false;
        await c.ExecuteAsync("UPDATE api_keys SET revoked_at = @at WHERE id = @id;", new { id, at = Clock.Now() });
        await AuditLog.WriteAsync(c, actor, "key.revoked", key.KeyPrefix, key.Name);
        return true;
    }

    public async Task<TenantApiKey?> FindAsync(TenantRecord tenant, string secret)
    {
        await using var c = await tenantDb.OpenAsync(tenant);
        var key = await c.QueryFirstOrDefaultAsync<TenantApiKey>("SELECT * FROM api_keys WHERE key_hash = @hash;", new { hash = Hash(secret) });
        if (key is { IsUsable: true })
            await c.ExecuteAsync("UPDATE api_keys SET last_used_at = @at WHERE id = @id;", new { id = key.Id, at = Clock.Now() });
        return key;
    }

    public async Task LogRequestAsync(TenantRecord tenant, ApiRequestLog log)
    {
        await using var c = await tenantDb.OpenAsync(tenant);
        await c.ExecuteAsync(
            "INSERT INTO api_requests (at, key_id, key_name, livemode, method, path, status, duration_ms, idempotency_key, ip, error) " +
            "VALUES (@At, @KeyId, @KeyName, @Livemode, @Method, @Path, @Status, @DurationMs, @IdempotencyKey, @Ip, @Error);", log);
    }

    public async Task<IReadOnlyList<ApiRequestLog>> RequestsAsync(TenantRecord tenant, int take = 100)
    {
        await using var c = await tenantDb.OpenAsync(tenant);
        return (await c.QueryAsync<ApiRequestLog>($"SELECT * FROM api_requests ORDER BY id DESC LIMIT {take};")).ToList();
    }
}
