using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using PayLink.Infrastructure;
using PayLink.Platform;
using PayLink.Tenancy;

namespace PayLink.Identity;

public static class AuthSchemes
{
    public const string Admin = "PayLinkAdmin";
    public const string Console = "PayLinkConsole";
    public const string DeveloperKey = "PayLinkDeveloperKey";
}

public static class PayLinkClaims
{
    public const string Subject = "sub";
    public const string Email = "email";
    public const string Name = "name";
    public const string Access = "paylink_access";   // admin | support (platform)
    public const string Tenant = "paylink_tenant";   // tenant slug (console)
    public const string Role = "paylink_role";       // owner | admin | developer | finance | viewer
}

public sealed record AdmissionResult(bool Allowed, string? Grant, string Message)
{
    public static AdmissionResult In(string grant) => new(true, grant, "");
    public static AdmissionResult Waiting(string message) => new(false, null, message);
}

/// <summary>
/// The second check after LoginLink: being signed in proves who someone is; admission decides whether they get in.
/// Modes are the same as CrudApi's: only people I approve, auto-approve LoginLink tenant admins, anyone holding a role,
/// and (tenant consoles only) anyone in the organization.
/// </summary>
public sealed class AdmissionService(PlatformRepository platform, PlatformSettings settings, TenantDb tenantDb, PlatformDb platformDb)
{
    public async Task<AdmissionResult> AdmitPlatformAsync(SignedInPerson person)
    {
        var mode = await settings.GetAdminAdmissionAsync();
        var existing = await platform.AdminBySubjectAsync(person.Subject);
        var now = Clock.Now();

        string? roleAccess = person.Roles.Contains(AdmissionModes.PlatformAdminRole, StringComparer.OrdinalIgnoreCase) ? "admin"
            : person.Roles.Contains(AdmissionModes.PlatformSupportRole, StringComparer.OrdinalIgnoreCase) ? "support" : null;

        if (existing is { Status: "approved" })
        {
            // In role mode, people let in by their role lose access as soon as the role is removed in LoginLink.
            if (mode == AdmissionModes.Role && existing.Source == "auto")
            {
                if (roleAccess is null)
                {
                    existing.Status = "removed"; existing.DecidedBy = "role removed in LoginLink"; existing.DecidedAt = now;
                    await platform.UpsertAdminAsync(existing);
                    return AdmissionResult.Waiting("Your LoginLink account no longer has a PayLink platform role.");
                }
                existing.Access = roleAccess;
            }
            existing.Email = person.Email ?? existing.Email; existing.Name = person.Name ?? existing.Name; existing.LastSignInAt = now;
            await platform.UpsertAdminAsync(existing);
            return AdmissionResult.In(existing.Access);
        }
        if (existing is { Status: "removed" } && mode == AdmissionModes.ApproveOnly)
            return AdmissionResult.Waiting("Your access to this platform console was removed. Ask a platform admin to restore it.");

        string? autoAccess = mode switch
        {
            AdmissionModes.AutoTenantAdmins when person.IsTenantAdmin => "admin",
            AdmissionModes.Role => roleAccess,
            _ => null,
        };

        var record = existing ?? new PlatformAdmin { Subject = person.Subject, CreatedAt = now };
        record.Email = person.Email; record.Name = person.Name;
        if (autoAccess is not null)
        {
            record.Status = "approved"; record.Source = "auto"; record.Access = autoAccess;
            record.DecidedBy = $"auto ({mode})"; record.DecidedAt = now; record.LastSignInAt = now;
            await platform.UpsertAdminAsync(record);
            await AuditPlatform(person, "admin.auto_approved", autoAccess);
            return AdmissionResult.In(autoAccess);
        }

        if (existing?.Status != "pending")
        {
            record.Status = "pending"; record.Source = "approved"; record.Access = "admin";
            await platform.UpsertAdminAsync(record);
            await AuditPlatform(person, "admin.access_requested", null);
        }
        return AdmissionResult.Waiting("You're signed in, but not yet allowed into the platform console. A platform admin has been asked to approve you.");
    }

    public async Task<AdmissionResult> AdmitTenantAsync(TenantRecord tenant, SignedInPerson person)
    {
        if (!tenant.IsActive)
            return AdmissionResult.Waiting("This PayLink account is suspended. Contact your platform administrator.");

        var mode = await tenantDb.GetAdmissionModeAsync(tenant);
        var existing = await tenantDb.MemberBySubjectAsync(tenant, person.Subject);
        var now = Clock.Now();
        var mapped = await MappedRoleAsync(tenant, person);

        if (existing is { Status: "approved" })
        {
            if (mode == AdmissionModes.Role && existing.Source == "auto")
            {
                if (mapped is null)
                {
                    existing.Status = "removed"; existing.DecidedBy = "role removed in LoginLink"; existing.DecidedAt = now;
                    await tenantDb.UpsertMemberAsync(tenant, existing);
                    return AdmissionResult.Waiting("Your LoginLink account no longer has a PayLink role in this organization.");
                }
                existing.Role = mapped;
            }
            existing.Email = person.Email ?? existing.Email; existing.Name = person.Name ?? existing.Name; existing.LastSignInAt = now;
            await tenantDb.UpsertMemberAsync(tenant, existing);
            return AdmissionResult.In(existing.Role);
        }
        if (existing is { Status: "removed" } && mode == AdmissionModes.ApproveOnly)
            return AdmissionResult.Waiting("Your access to this console was removed. Ask an owner or admin to restore it.");

        string? autoRole = mode switch
        {
            AdmissionModes.AutoTenantAdmins when person.IsTenantAdmin => TenantRoles.Admin,
            AdmissionModes.Role => mapped,
            AdmissionModes.Anyone => mapped ?? TenantRoles.Viewer,
            _ => null,
        };

        var member = existing ?? new Member { Subject = person.Subject, CreatedAt = now };
        member.Email = person.Email; member.Name = person.Name;
        if (autoRole is not null)
        {
            member.Status = "approved"; member.Source = "auto"; member.Role = autoRole;
            member.DecidedBy = $"auto ({mode})"; member.DecidedAt = now; member.LastSignInAt = now;
            await tenantDb.UpsertMemberAsync(tenant, member);
            await tenantDb.AuditAsync(tenant, Actor.Person(person.Subject, person.Display, null), "member.auto_approved", person.Display, autoRole);
            return AdmissionResult.In(autoRole);
        }

        if (existing?.Status != "pending")
        {
            member.Status = "pending"; member.Source = "approved"; member.Role = TenantRoles.Viewer;
            await tenantDb.UpsertMemberAsync(tenant, member);
            await tenantDb.AuditAsync(tenant, Actor.Person(person.Subject, person.Display, null), "member.access_requested", person.Display);
        }
        return AdmissionResult.Waiting($"You're signed in, but not yet allowed into {tenant.Name}'s console. An owner or admin has been asked to approve you.");
    }

    private async Task<string?> MappedRoleAsync(TenantRecord tenant, SignedInPerson person)
    {
        var map = await tenantDb.RoleMapAsync(tenant);
        var granted = map.Where(m => person.Roles.Contains(m.LoginLinkRole, StringComparer.OrdinalIgnoreCase))
            .Select(m => m.PayLinkRole).ToList();
        // The most powerful mapped role wins.
        return TenantRoles.All.FirstOrDefault(granted.Contains);
    }

    private Task AuditPlatform(SignedInPerson person, string action, string? details) =>
        platformDb.AuditAsync(Actor.Person(person.Subject, person.Display, null), action, person.Display, details);
}

/// <summary>Builds the cookie sign-ins for the two consoles.</summary>
public static class SignInHelper
{
    public static Task SignInAdminAsync(HttpContext http, SignedInPerson person, string access) =>
        http.SignInAsync(AuthSchemes.Admin, Principal(AuthSchemes.Admin, person, [new(PayLinkClaims.Access, access)]));

    public static Task SignInConsoleAsync(HttpContext http, SignedInPerson person, string tenantSlug, string role) =>
        http.SignInAsync(AuthSchemes.Console, Principal(AuthSchemes.Console, person, [new(PayLinkClaims.Tenant, tenantSlug), new(PayLinkClaims.Role, role)]));

    private static ClaimsPrincipal Principal(string scheme, SignedInPerson person, List<Claim> extra)
    {
        var claims = new List<Claim> { new(PayLinkClaims.Subject, person.Subject) };
        if (person.Email is not null) claims.Add(new(PayLinkClaims.Email, person.Email));
        claims.Add(new(PayLinkClaims.Name, person.Name ?? person.Email ?? person.Subject));
        claims.AddRange(extra);
        return new ClaimsPrincipal(new ClaimsIdentity(claims, scheme, PayLinkClaims.Name, PayLinkClaims.Role));
    }

    public static string? Get(this ClaimsPrincipal user, string type) => user.FindFirst(type)?.Value;

    public static Actor ToActor(this ClaimsPrincipal user, HttpContext http) =>
        Actor.Person(user.Get(PayLinkClaims.Subject) ?? "?", user.Get(PayLinkClaims.Email) ?? user.Get(PayLinkClaims.Name), http.Connection.RemoteIpAddress?.ToString());
}

public static class CookieDefaults
{
    public static void Configure(CookieAuthenticationOptions o, string name, string path, string loginPath)
    {
        o.Cookie.Name = name;
        o.Cookie.Path = path;
        o.Cookie.HttpOnly = true;
        o.Cookie.SameSite = SameSiteMode.Lax;
        o.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
        o.ExpireTimeSpan = TimeSpan.FromHours(8);
        o.SlidingExpiration = true;
        o.LoginPath = loginPath;
        o.AccessDeniedPath = loginPath;
    }
}
