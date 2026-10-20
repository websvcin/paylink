namespace PayLink.Platform;

public sealed class TenantRecord
{
    public long Id { get; set; }
    public string Slug { get; set; } = "";
    public string Name { get; set; } = "";
    public string Status { get; set; } = "active";
    public string DbMode { get; set; } = "";
    public string DbEngine { get; set; } = "";
    public string DbDisplay { get; set; } = "";
    public string DbConnectionEnc { get; set; } = "";
    public string? DbProviderRefEnc { get; set; }
    public string LoginlinkOrg { get; set; } = "";
    public string LoginlinkIssuer { get; set; } = "";
    public string LoginlinkMode { get; set; } = "";
    public string? ConsoleAppId { get; set; }
    public string ConsoleClientId { get; set; } = "";
    public string ConsoleClientSecretEnc { get; set; } = "";
    public string? MgmtClientId { get; set; }
    public string? MgmtClientSecretEnc { get; set; }
    public string ApiRegistration { get; set; } = "skipped";
    public string? ApiRegistrationError { get; set; }
    public string CreatedAt { get; set; } = "";
    public string? CreatedBy { get; set; }
    public string CreatedVia { get; set; } = "";
    public long? CreatedByKeyId { get; set; }
    /// <summary>"path" (/{slug}/console) or "subdomain" ({slug}.{tenant domain}).</summary>
    public string ConsoleMode { get; set; } = "path";

    public bool IsActive => Status == "active";
}

public sealed class PlatformAdmin
{
    public long Id { get; set; }
    public string Subject { get; set; } = "";
    public string? Email { get; set; }
    public string? Name { get; set; }
    public string Access { get; set; } = "admin";
    public string Status { get; set; } = "";
    public string Source { get; set; } = "";
    public string CreatedAt { get; set; } = "";
    public string? DecidedBy { get; set; }
    public string? DecidedAt { get; set; }
    public string? LastSignInAt { get; set; }
}

public sealed class AutoProvisionApproval
{
    public long Id { get; set; }
    public string OrgSlug { get; set; } = "";
    public string? Note { get; set; }
    public string CreatedAt { get; set; } = "";
    public string? CreatedBy { get; set; }
    public string? UsedAt { get; set; }
}

public sealed class DeveloperKey
{
    public long Id { get; set; }
    public string Name { get; set; } = "";
    public string KeyPrefix { get; set; } = "";
    public string KeyHash { get; set; } = "";
    public string Capabilities { get; set; } = "";
    public string Reach { get; set; } = "all";
    public string? TenantIds { get; set; }
    public int? CreationLimit { get; set; }
    public string? ExpiresAt { get; set; }
    public string CreatedAt { get; set; } = "";
    public string? CreatedBy { get; set; }
    public string? RevokedAt { get; set; }
    public string? LastUsedAt { get; set; }

    public IReadOnlyList<string> CapabilityList => Capabilities.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    public bool IsUsable => RevokedAt is null && (Infrastructure.Clock.Parse(ExpiresAt) is not { } exp || exp > DateTime.UtcNow);
}

/// <summary>Platform developer key capabilities. Each one maps to a fixed set of /api/admin routes.</summary>
public static class Capabilities
{
    public const string Tenants = "Tenants";
    public const string AutoProvisioning = "AutoProvisioning";
    public const string DatabaseHosting = "DatabaseHosting";
    public const string LoginLink = "LoginLink";
    public const string Settings = "Settings";
    public const string Audit = "Audit";
    public const string Dashboard = "Dashboard";

    public static readonly IReadOnlyList<(string Name, string Label, string Covers)> All =
    [
        (Tenants, "Tenants", "Create, list, look up, suspend and reactivate tenants"),
        (AutoProvisioning, "Auto-provisioning", "The approval list of LoginLink organizations"),
        (DatabaseHosting, "Database hosting", "Read the hosting environment summary"),
        (LoginLink, "LoginLink", "Connect LoginLink organizations when creating tenants"),
        (Settings, "Limits and settings", "Read platform settings"),
        (Audit, "Audit", "Read the platform audit log"),
        (Dashboard, "Dashboard", "Read-only platform counts"),
    ];
}

/// <summary>PayLink roles inside a tenant console, in order of power.</summary>
public static class TenantRoles
{
    public const string Owner = "owner";
    public const string Admin = "admin";
    public const string Developer = "developer";
    public const string Finance = "finance";
    public const string Viewer = "viewer";

    public static readonly string[] All = [Owner, Admin, Developer, Finance, Viewer];

    public static bool CanManageTeam(string role) => role is Owner or Admin;
}
