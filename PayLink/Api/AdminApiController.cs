using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PayLink.Identity;
using PayLink.Infrastructure;
using PayLink.Platform;
using PayLink.Tenancy;
using WebSvc.PayLink.Storage;

namespace PayLink.Api;

/// <summary>
/// The Platform Developer API. Authenticated with platform developer keys (pdk_); each route needs one capability.
/// Errors use LoginLink's shape: { "error": "...", "error_description": "..." }.
/// </summary>
[ApiController]
[Route("api/admin")]
[Authorize(Policy = "DeveloperKey")]
public sealed class AdminApiController(
    PlatformRepository repo,
    PlatformDb platformDb,
    PlatformSettings settings,
    TenantDb tenantDb,
    OrgConnector orgs,
    TenantProvisioning provisioning) : ControllerBase
{
    private long KeyId => long.Parse(User.FindFirst(DeveloperKeyHandler.KeyIdClaim)!.Value);
    private string KeyName => User.FindFirst(DeveloperKeyHandler.KeyNameClaim)!.Value;
    private bool OwnTenantsOnly => User.FindFirst(DeveloperKeyHandler.ReachClaim)?.Value == "own";
    private Actor Me => Actor.Key(KeyId, KeyName, HttpContext.Connection.RemoteIpAddress?.ToString());

    private static ObjectResult Error(int status, string error, string description) =>
        new(new { error, error_description = description }) { StatusCode = status };

    private static object Shape(TenantRecord t) => new
    {
        id = t.Id, slug = t.Slug, name = t.Name, status = t.Status,
        database = new { mode = t.DbMode, engine = t.DbEngine, display = t.DbDisplay },
        loginLink = new { organization = t.LoginlinkOrg, issuer = t.LoginlinkIssuer, mode = t.LoginlinkMode, apiRegistration = t.ApiRegistration },
        consoleUrl = $"/{t.Slug}/console", createdAt = t.CreatedAt, createdVia = t.CreatedVia,
    };

    private bool InReach(TenantRecord t) => !OwnTenantsOnly || t.CreatedByKeyId == KeyId;

    /// <summary>Platform counts: tenants, admins, approvals, keys, and live payments in the last 7 days.</summary>
    [HttpGet("dashboard")]
    [RequireCapability(Capabilities.Dashboard)]
    public async Task<IActionResult> Dashboard()
    {
        var c = await repo.CountsAsync();
        return Ok(new { activeTenants = c.Active, suspendedTenants = c.Suspended, platformAdmins = c.Admins, pendingAdmins = c.PendingAdmins, approvals = c.Approvals, developerKeys = c.Keys });
    }

    /// <summary>Tenants in this key's reach; q searches name, address and LoginLink organization.</summary>
    [HttpGet("tenants")]
    [RequireCapability(Capabilities.Tenants)]
    public async Task<IActionResult> Tenants([FromQuery] string? q, [FromQuery] int take = 50)
    {
        var list = await repo.TenantsAsync(q, OwnTenantsOnly ? KeyId : null, Math.Clamp(take, 1, 200));
        return Ok(new { tenants = list.Select(Shape) });
    }

    /// <summary>One tenant.</summary>
    [HttpGet("tenants/{id:long}")]
    [RequireCapability(Capabilities.Tenants)]
    public async Task<IActionResult> Tenant(long id)
    {
        var t = await repo.TenantByIdAsync(id);
        return t is null || !InReach(t) ? Error(404, "not_found", "No such tenant.") : Ok(Shape(t));
    }

    public sealed record StatusBody(string Status);

    /// <summary>Suspends ("suspended") or reactivates ("active") a tenant.</summary>
    [HttpPatch("tenants/{id:long}")]
    [RequireCapability(Capabilities.Tenants)]
    public async Task<IActionResult> SetStatus(long id, [FromBody] StatusBody body)
    {
        if (body.Status is not ("active" or "suspended")) return Error(400, "invalid_request", "status must be \"active\" or \"suspended\".");
        var t = await repo.TenantByIdAsync(id);
        if (t is null || !InReach(t)) return Error(404, "not_found", "No such tenant.");
        await repo.SetTenantStatusAsync(id, body.Status);
        var action = body.Status == "active" ? "tenant.reactivated" : "tenant.suspended";
        await platformDb.AuditAsync(Me, action, t.Slug);
        await tenantDb.AuditAsync(t, Me, action, t.Slug, "by platform developer key");
        return Ok(new { id, status = body.Status });
    }

    public sealed record CreateTenantBody(string Name, string Slug, CreateTenantLoginLink LoginLink, CreateTenantDatabase? Database);
    public sealed record CreateTenantLoginLink(string Organization, string ClientId, string ClientSecret);
    /// <summary>Mode sqlite, auto or byo; for byo, Engine mysql (default) or postgresql and its connection string.</summary>
    public sealed record CreateTenantDatabase(string Mode, string? ConnectionString, string? Engine = null);

    /// <summary>Creates a tenant for an existing LoginLink organization. Needs Tenants and LoginLink.</summary>
    [HttpPost("tenants")]
    [RequireCapability(Capabilities.Tenants)]
    [RequireCapability(Capabilities.LoginLink)]
    public async Task<IActionResult> CreateTenant([FromBody] CreateTenantBody body)
    {
        var key = (await repo.DeveloperKeysAsync()).First(k => k.Id == KeyId);
        if (key.CreationLimit is { } limit && await repo.CountTenantsCreatedByKeyAsync(KeyId) >= limit)
            return Error(429, "quota_exceeded", $"This key may create at most {limit} tenant(s).");
        if (await provisioning.CheckNameAndSlugAsync(body.Name, body.Slug) is { } problem) return Error(400, "invalid_request", problem);

        var mode = (body.Database?.Mode ?? "sqlite").ToLowerInvariant();
        DatabaseTarget? byo = null;
        if (mode == "byo")
        {
            if (string.Equals(body.Database?.Engine, "postgresql", StringComparison.OrdinalIgnoreCase))
            {
                if (!PostgreSqlConnectionDetails.TryParse(body.Database?.ConnectionString ?? "", out var pg, out var pgError))
                    return Error(400, "invalid_request", pgError);
                byo = DatabaseTarget.ForPostgreSql(pg!);
            }
            else
            {
                if (!MySqlConnectionDetails.TryParse(body.Database?.ConnectionString ?? "", out var details, out var parseError))
                    return Error(400, "invalid_request", parseError);
                byo = DatabaseTarget.ForMySql(details!);
            }
        }
        else if (mode is not ("sqlite" or "auto")) return Error(400, "invalid_request", "database.mode must be sqlite, auto or byo.");

        try
        {
            var org = await orgs.ConnectExistingAsync(body.LoginLink.Organization, body.LoginLink.ClientId, body.LoginLink.ClientSecret);
            var dbMode = mode switch { "auto" => DatabaseMode.Auto, "byo" => DatabaseMode.Byo, _ => DatabaseMode.Sqlite };
            var tenant = await provisioning.CreateAsync(new TenantCreateRequest(body.Name, body.Slug, org, new DatabaseChoice(dbMode, byo), null, "api", Me, KeyId));
            return StatusCode(201, Shape(tenant));
        }
        catch (Exception ex) when (ex is LoginLinkException or TenantCreateException)
        {
            return Error(400, "invalid_request", ex.Message);
        }
    }

    // ---------- tenants' keys (TenantOperations) ----------

    public sealed record TenantKeyBody(string Name, string Kind, bool Livemode, List<string>? Capabilities, [property: System.Text.Json.Serialization.JsonPropertyName("expires_at")] string? ExpiresAt);

    /// <summary>A tenant's keys (secret keys for /api/v1, developer keys for /api/tenant). Secrets are never listed.</summary>
    [HttpGet("tenants/{id:long}/keys")]
    [RequireCapability(Capabilities.TenantOperations)]
    public async Task<IActionResult> TenantKeys(long id, [FromServices] PayLink.Payments.TenantKeyService keys)
    {
        var t = await repo.TenantByIdAsync(id);
        if (t is null || !InReach(t)) return Error(404, "not_found", "No such tenant.");
        return Ok(new
        {
            data = (await keys.ListAsync(t)).Select(k => new
            {
                k.Id, k.Name, k.Kind, k.Livemode, prefix = k.KeyPrefix, capabilities = k.CapabilityList, k.CreatedAt, k.CreatedBy, k.LastUsedAt, k.ExpiresAt, k.RevokedAt,
            }),
        });
    }

    /// <summary>
    /// Creates a key for a tenant, e.g. to hand a product built on PayLink its own sk_ key, or a tdk_ key with chosen capabilities.
    /// The secret is returned once. Recorded in the tenant's and the platform's audit logs.
    /// </summary>
    [HttpPost("tenants/{id:long}/keys")]
    [RequireCapability(Capabilities.TenantOperations)]
    public async Task<IActionResult> CreateTenantKey(long id, [FromBody] TenantKeyBody body, [FromServices] PayLink.Payments.TenantKeyService keys)
    {
        var t = await repo.TenantByIdAsync(id);
        if (t is null || !InReach(t)) return Error(404, "not_found", "No such tenant.");
        if (string.IsNullOrWhiteSpace(body.Name)) return Error(400, "invalid_request", "name is needed.");
        if (body.Kind is not (PayLink.Payments.TenantKeyKinds.Secret or PayLink.Payments.TenantKeyKinds.Developer)) return Error(400, "invalid_request", "kind is secret or developer.");
        var caps = body.Capabilities ?? [];
        if (body.Kind == PayLink.Payments.TenantKeyKinds.Developer)
        {
            if (caps.Count == 0) return Error(400, "invalid_request", "A developer key needs at least one capability.");
            if (caps.FirstOrDefault(c => !PayLink.Payments.TenantCapabilities.IsKnown(c)) is { } unknown) return Error(400, "invalid_request", $"Unknown capability {unknown}.");
        }
        var (key, secret) = await keys.CreateAsync(t, body.Kind, body.Name, body.Livemode, caps, body.ExpiresAt, Me);
        await platformDb.AuditAsync(Me, "tenant.key_created", t.Slug, $"{key.Name} ({key.Kind})");
        return StatusCode(201, new { key.Id, key.Name, key.Kind, key.Livemode, prefix = key.KeyPrefix, capabilities = key.CapabilityList, secret });
    }

    /// <summary>Revokes one of a tenant's keys.</summary>
    [HttpDelete("tenants/{id:long}/keys/{keyId:long}")]
    [RequireCapability(Capabilities.TenantOperations)]
    public async Task<IActionResult> RevokeTenantKey(long id, long keyId, [FromServices] PayLink.Payments.TenantKeyService keys)
    {
        var t = await repo.TenantByIdAsync(id);
        if (t is null || !InReach(t)) return Error(404, "not_found", "No such tenant.");
        if (!await keys.RevokeAsync(t, keyId, Me)) return Error(404, "not_found", $"No active key {keyId}.");
        await platformDb.AuditAsync(Me, "tenant.key_revoked", t.Slug, keyId.ToString());
        return NoContent();
    }

    /// <summary>LoginLink organizations approved for an auto-provisioned database at signup.</summary>
    [HttpGet("auto-provisioning/approvals")]
    [RequireCapability(Capabilities.AutoProvisioning)]
    public async Task<IActionResult> Approvals() =>
        Ok(new { mode = await settings.GetAutoProvisionModeAsync(), approvals = (await repo.ApprovalsAsync()).Select(a => new { organization = a.OrgSlug, a.Note, a.CreatedAt, a.UsedAt }) });

    public sealed record ApprovalBody(string Organization, string? Note);

    /// <summary>Approves an organization for an auto-provisioned database at signup.</summary>
    [HttpPost("auto-provisioning/approvals")]
    [RequireCapability(Capabilities.AutoProvisioning)]
    public async Task<IActionResult> AddApproval([FromBody] ApprovalBody body)
    {
        var slug = (body.Organization ?? "").Trim().ToLowerInvariant();
        if (slug.Length == 0) return Error(400, "invalid_request", "organization is required.");
        if (!await repo.AddApprovalAsync(slug, body.Note, KeyName)) return Error(409, "conflict", $"{slug} is already approved.");
        await platformDb.AuditAsync(Me, "autoprov.approved", slug, body.Note);
        return StatusCode(201, new { organization = slug });
    }

    /// <summary>Removes an approval.</summary>
    [HttpDelete("auto-provisioning/approvals/{organization}")]
    [RequireCapability(Capabilities.AutoProvisioning)]
    public async Task<IActionResult> RemoveApproval(string organization)
    {
        if (!await repo.RemoveApprovalAsync(organization.ToLowerInvariant())) return Error(404, "not_found", "That organization isn't approved.");
        await platformDb.AuditAsync(Me, "autoprov.removed", organization);
        return NoContent();
    }

    /// <summary>The platform audit log, newest first.</summary>
    [HttpGet("audit")]
    [RequireCapability(Capabilities.Audit)]
    public async Task<IActionResult> Audit([FromQuery] int take = 100, [FromQuery] int skip = 0)
    {
        await using var c = await platformDb.OpenAsync();
        var entries = await AuditLog.RecentAsync(c, Math.Clamp(take, 1, 500), Math.Max(0, skip));
        return Ok(new { entries = entries.Select(e => new { e.Id, e.At, actor = new { type = e.ActorType, id = e.ActorId, name = e.ActorName, ip = e.Ip }, e.Action, e.Target, e.Details, e.Hash }) });
    }
}
