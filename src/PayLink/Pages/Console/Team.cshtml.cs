using Microsoft.AspNetCore.Mvc;
using PayLink.Identity;
using PayLink.Infrastructure;
using PayLink.Platform;
using PayLink.Tenancy;

namespace PayLink.Pages.Console;

/// <summary>Who may sign in to this tenant's console, the people list, and the LoginLink role map.</summary>
public sealed class TeamModel : ConsolePage
{
    public string Mode { get; private set; } = AdmissionModes.AutoTenantAdmins;
    public IReadOnlyList<Member> Members { get; private set; } = [];
    public IReadOnlyList<(string LoginLinkRole, string PayLinkRole)> RoleMap { get; private set; } = [];

    public async Task OnGetAsync()
    {
        Mode = await TenantDb.GetAdmissionModeAsync(Tenant);
        Members = await TenantDb.MembersAsync(Tenant);
        RoleMap = await TenantDb.RoleMapAsync(Tenant);
    }

    public async Task<IActionResult> OnPostModeAsync(string mode)
    {
        if (!CanManageTeam) return Failed("Only owners and admins can change who may sign in.");
        if (mode is not (AdmissionModes.ApproveOnly or AdmissionModes.AutoTenantAdmins or AdmissionModes.Role or AdmissionModes.Anyone)) return Failed("Unknown mode.");
        await TenantDb.SetSettingAsync(Tenant, "admission_mode", mode);
        await TenantDb.AuditAsync(Tenant, Me, "team.admission_mode_changed", mode);
        return Done("Saved. The new rule applies at each person's next sign-in.");
    }

    public async Task<IActionResult> OnPostMemberAsync(long id, string decision, string? role)
    {
        if (!CanManageTeam) return Failed("Only owners and admins can change the team.");
        var member = await TenantDb.MemberByIdAsync(Tenant, id);
        if (member is null) return Failed("That person isn't in the list.");
        var mine = member.Subject == User.Get(PayLinkClaims.Subject);

        if (decision == "remove")
        {
            if (mine) return Failed("You can't remove yourself.");
            if (member.Role == TenantRoles.Owner && await TenantDb.CountOwnersAsync(Tenant) <= 1) return Failed("This is the last owner. Make someone else an owner first.");
            member.Status = "removed";
        }
        else if (decision == "approve")
        {
            if (role is null || !TenantRoles.All.Contains(role)) return Failed("Choose a role.");
            if (role == TenantRoles.Owner && Role != TenantRoles.Owner) return Failed("Only an owner can make someone an owner.");
            if (mine && role != member.Role) return Failed("You can't change your own role.");
            if (member.Role == TenantRoles.Owner && role != TenantRoles.Owner && await TenantDb.CountOwnersAsync(Tenant) <= 1)
                return Failed("This is the last owner. Make someone else an owner first.");
            member.Status = "approved";
            member.Role = role;
            member.Source = "approved";
        }
        else return Failed("Unknown decision.");

        member.DecidedBy = MyEmail;
        member.DecidedAt = Clock.Now();
        await TenantDb.UpsertMemberAsync(Tenant, member);
        await TenantDb.AuditAsync(Tenant, Me, decision == "remove" ? "member.removed" : "member.approved", member.Email ?? member.Subject, member.Role);
        return Done(decision == "remove" ? $"{member.Email} no longer has access." : $"{member.Email} is now {member.Role}.");
    }

    public async Task<IActionResult> OnPostRoleMapAsync(string loginLinkRole, string? payLinkRole)
    {
        if (!CanManageTeam) return Failed("Only owners and admins can change the role map.");
        loginLinkRole = (loginLinkRole ?? "").Trim();
        if (loginLinkRole.Length is 0 or > 100) return Failed("Enter the LoginLink role name.");
        if (payLinkRole is not null && payLinkRole != "" && !TenantRoles.All.Contains(payLinkRole)) return Failed("Unknown PayLink role.");
        await TenantDb.SetRoleMapAsync(Tenant, loginLinkRole, string.IsNullOrEmpty(payLinkRole) ? null : payLinkRole);
        await TenantDb.AuditAsync(Tenant, Me, "team.role_map_changed", loginLinkRole, payLinkRole ?? "removed");
        return Done("Role map saved.");
    }
}
