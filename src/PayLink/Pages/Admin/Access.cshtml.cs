using Microsoft.AspNetCore.Mvc;
using PayLink.Identity;
using PayLink.Infrastructure;
using PayLink.Platform;

namespace PayLink.Pages.Admin;

/// <summary>Who may sign in to /admin: the admission mode and the people list.</summary>
public sealed class AccessModel(PlatformRepository repo, PlatformSettings settings, PlatformDb platformDb) : AdminPage
{
    public string Mode { get; private set; } = AdmissionModes.ApproveOnly;
    public string PlatformOrg { get; private set; } = "";
    public IReadOnlyList<PlatformAdmin> People { get; private set; } = [];

    public async Task OnGetAsync()
    {
        Mode = await settings.GetAdminAdmissionAsync();
        PlatformOrg = (await settings.GetLoginLinkAsync())?.PlatformOrg ?? "";
        People = await repo.AdminsAsync();
    }

    public async Task<IActionResult> OnPostModeAsync(string mode)
    {
        if (mode is not (AdmissionModes.ApproveOnly or AdmissionModes.AutoTenantAdmins or AdmissionModes.Role)) return Failed("Unknown mode.");
        await settings.SetAsync(PlatformSettings.KeyAdminAdmission, mode);
        await platformDb.AuditAsync(Me, "admin.admission_mode_changed", mode);
        return Done("Saved. The new rule applies at each person's next sign-in.");
    }

    public async Task<IActionResult> OnPostDecideAsync(string subject, string decision, string? access)
    {
        var person = await repo.AdminBySubjectAsync(subject);
        if (person is null) return Failed("That person isn't in the list.");
        if (subject == User.Get(PayLinkClaims.Subject) && decision != "approve") return Failed("You can't remove or downgrade your own access.");

        var approved = (await repo.AdminsAsync()).Count(a => a.Status == "approved" && a.Access == "admin");
        if (person is { Status: "approved", Access: "admin" } && approved <= 1 && (decision == "remove" || access == "support"))
            return Failed("This is the last platform admin. Approve another admin first.");

        person.DecidedBy = User.Get(PayLinkClaims.Email);
        person.DecidedAt = Clock.Now();
        switch (decision)
        {
            case "approve":
                person.Status = "approved"; person.Source = "approved"; person.Access = access == "support" ? "support" : "admin";
                break;
            case "remove":
                person.Status = "removed";
                break;
            default:
                return Failed("Unknown decision.");
        }
        await repo.UpsertAdminAsync(person);
        await platformDb.AuditAsync(Me, $"admin.{decision}d", person.Email ?? person.Subject, person.Access);
        return Done(decision == "remove" ? $"{person.Email} no longer has access." : $"{person.Email} now has {person.Access} access.");
    }
}
