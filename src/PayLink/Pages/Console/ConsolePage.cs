using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PayLink.Identity;
using PayLink.Infrastructure;
using PayLink.Platform;
using PayLink.Tenancy;

namespace PayLink.Pages.Console;

/// <summary>Shared behaviour for /{slug}/console pages: the tenant must exist and be active, and the person must be its approved member.</summary>
public abstract class ConsolePage : PageModel
{
    [FromRoute] public string Slug { get; set; } = "";

    public TenantRecord Tenant { get; private set; } = null!;
    public string Role { get; private set; } = TenantRoles.Viewer;
    public string MyEmail { get; private set; } = "";
    public bool CanManageTeam => TenantRoles.CanManageTeam(Role);
    protected TenantDb TenantDb => HttpContext.RequestServices.GetRequiredService<TenantDb>();
    protected Actor Me => User.ToActor(HttpContext);

    public override async Task OnPageHandlerExecutionAsync(PageHandlerExecutingContext context, PageHandlerExecutionDelegate next)
    {
        var repo = HttpContext.RequestServices.GetRequiredService<PlatformRepository>();
        var tenant = await repo.TenantBySlugAsync(Slug.ToLowerInvariant());
        if (tenant is null) { context.Result = NotFound(); return; }

        var auth = await HttpContext.AuthenticateAsync(AuthSchemes.Console);
        var user = auth.Principal;
        if (!auth.Succeeded || user?.Get(PayLinkClaims.Tenant) != tenant.Slug)
        {
            context.Result = Redirect($"/{tenant.Slug}/login");
            return;
        }
        if (!tenant.IsActive)
        {
            await HttpContext.SignOutAsync(AuthSchemes.Console);
            TempData["Error"] = "This PayLink account is suspended. Contact your platform administrator.";
            context.Result = Redirect($"/{tenant.Slug}/login");
            return;
        }

        // Re-check membership on every request so removing someone takes effect immediately.
        var member = await TenantDb.MemberBySubjectAsync(tenant, user.Get(PayLinkClaims.Subject) ?? "");
        if (member is not { Status: "approved" })
        {
            await HttpContext.SignOutAsync(AuthSchemes.Console);
            context.Result = Redirect($"/{tenant.Slug}/login");
            return;
        }

        HttpContext.User = user;
        Tenant = tenant;
        Role = member.Role;
        MyEmail = member.Email ?? member.Name ?? member.Subject;
        ViewData["Tenant"] = tenant;
        ViewData["Role"] = Role;
        ViewData["MyEmail"] = MyEmail;
        ViewData["PendingMembers"] = (await TenantDb.MembersAsync(tenant)).Count(m => m.Status == "pending");
        await next();
    }

    protected IActionResult Done(string message)
    {
        TempData["Ok"] = message;
        return Redirect(Request.Path);
    }

    protected IActionResult Failed(string message)
    {
        TempData["Error"] = message;
        return Redirect(Request.Path);
    }
}
