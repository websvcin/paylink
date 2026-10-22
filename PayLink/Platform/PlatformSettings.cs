using Dapper;
using PayLink.Infrastructure;

namespace PayLink.Platform;

/// <summary>How PayLink reaches LoginLink. Stored encrypted as one settings value.</summary>
public sealed class LoginLinkSettings
{
    /// <summary>Base address of the LoginLink server, e.g. https://loginlink.websvc.in (no trailing slash).</summary>
    public string ServerUrl { get; set; } = "";

    /// <summary>Slug of the organization whose people may sign in to /admin. Empty means LoginLink's platform root organization.</summary>
    public string PlatformOrg { get; set; } = "";

    /// <summary>The "PayLink admin" app registered in the platform organization.</summary>
    public string AdminAppId { get; set; } = "";
    public string AdminClientId { get; set; } = "";
    public string AdminClientSecret { get; set; } = "";

    /// <summary>Optional LoginLink Platform API credential with CreateTenants, used for "Create a new organization" at signup.</summary>
    public string? PlatformApiClientId { get; set; }
    public string? PlatformApiClientSecret { get; set; }

    public bool CanCreateOrganizations => !string.IsNullOrEmpty(PlatformApiClientId) && !string.IsNullOrEmpty(PlatformApiClientSecret);
}

public static class AdmissionModes
{
    public const string ApproveOnly = "approve_only";
    public const string AutoTenantAdmins = "auto_tenant_admins";
    public const string Role = "role";
    public const string Anyone = "anyone";

    public const string PlatformAdminRole = "paylink_platform_admin";
    public const string PlatformSupportRole = "paylink_platform_support";
}

public static class AutoProvisionModes
{
    public const string Off = "off";
    public const string Approved = "approved";
    public const string Everyone = "everyone";
}

/// <summary>Typed access to the platform settings table.</summary>
public sealed class PlatformSettings(PlatformDb db, SecretProtector secrets)
{
    public const string KeyLoginLink = "loginlink";
    public const string KeyPublicBaseUrl = "public_base_url";
    public const string KeyAdminAdmission = "admin_admission";
    public const string KeySignupMode = "signup_mode";
    public const string KeyAutoProvisionMode = "autoprov_mode";
    public const string KeyAutoProvisionLimit = "autoprov_limit";
    public const string KeyTenantDbOptions = "tenant_db_options";
    public const string KeyLimitMaxAmount = "limit_max_amount";
    public const string KeyLimitCurrencies = "limit_currencies";
    public const string KeyTenantDomain = "tenant_domain";
    /// <summary>"off" hides the small "Secured by PayLink" badge on payer pages (white label).</summary>
    public const string KeyCheckoutBadge = "checkout_badge";
    /// <summary>The CrudApi server tenants are pointed at from Data access.</summary>
    public const string KeyCrudApiUrl = "crudapi_url";
    public const string DefaultCrudApiUrl = "https://crud.webapis.in";
    public const string CrudApiDocsUrl = "https://websvcin.github.io/crudapi/";

    public async Task<string> GetCrudApiUrlAsync() => (await GetAsync(KeyCrudApiUrl))?.Trim().TrimEnd('/') is { Length: > 0 } u ? u : DefaultCrudApiUrl;

    public async Task<string?> GetAsync(string key)
    {
        await using var c = await db.OpenAsync();
        return await c.ExecuteScalarAsync<string?>("SELECT svalue FROM settings WHERE skey = @key;", new { key });
    }

    public async Task SetAsync(string key, string? value)
    {
        await using var c = await db.OpenAsync();
        var dialect = await db.DialectAsync();
        await c.ExecuteAsync(dialect.Upsert("settings", "skey", "svalue", "updated_at"), new { skey = key, svalue = value, updated_at = Clock.Now() });
    }

    public async Task<LoginLinkSettings?> GetLoginLinkAsync() => secrets.UnprotectJson<LoginLinkSettings>(await GetAsync(KeyLoginLink));
    public Task SetLoginLinkAsync(LoginLinkSettings value) => SetAsync(KeyLoginLink, secrets.ProtectJson(value));

    public async Task<string> GetPublicBaseUrlAsync() => (await GetAsync(KeyPublicBaseUrl))?.TrimEnd('/') ?? "";

    public async Task<string> GetAdminAdmissionAsync() => await GetAsync(KeyAdminAdmission) ?? AdmissionModes.ApproveOnly;

    /// <summary>"open" lets anyone with a LoginLink account create a tenant at /signup; "off" leaves it to platform admins.</summary>
    public async Task<bool> IsSignupOpenAsync() => (await GetAsync(KeySignupMode) ?? "open") == "open";

    public async Task<string> GetAutoProvisionModeAsync() => await GetAsync(KeyAutoProvisionMode) ?? AutoProvisionModes.Approved;

    /// <summary>Which database choices tenants see at signup (sqlite, byo, auto). All of them unless an admin narrowed it.</summary>
    public async Task<IReadOnlySet<string>> GetTenantDbOptionsAsync()
    {
        var raw = await GetAsync(KeyTenantDbOptions);
        var all = TenantDbOptions.All.Select(o => o.Key);
        return (raw is null ? all : raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Intersect(all)).ToHashSet();
    }

    public Task SetTenantDbOptionsAsync(IEnumerable<string> options) => SetAsync(KeyTenantDbOptions, string.Join(",", options));

    public async Task<int> GetAutoProvisionLimitAsync() => int.TryParse(await GetAsync(KeyAutoProvisionLimit), out var n) ? n : 1;

    /// <summary>Platform limits (floors tenants can only tighten): the largest payment, in minor units, and the currencies allowed.</summary>
    public async Task<(long? MaxAmount, IReadOnlySet<string>? Currencies)> GetLimitsAsync()
    {
        var max = long.TryParse(await GetAsync(KeyLimitMaxAmount), out var m) && m > 0 ? m : (long?)null;
        var cur = (await GetAsync(KeyLimitCurrencies))?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(c => c.ToUpperInvariant()).ToHashSet();
        return (max, cur is { Count: > 0 } ? cur : null);
    }

    /// <summary>The wildcard domain tenants' consoles can live under, e.g. "paylink.websvc.in" for acme.paylink.websvc.in. Empty: subdomains are off.</summary>
    public async Task<string> GetTenantDomainAsync() => (await GetAsync(KeyTenantDomain))?.Trim().TrimStart('.').ToLowerInvariant() ?? "";
}
