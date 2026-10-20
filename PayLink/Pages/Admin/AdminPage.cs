using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PayLink.Identity;
using PayLink.Infrastructure;
using PayLink.Platform;

namespace PayLink.Pages.Admin;

/// <summary>Shared behaviour for /admin pages: support users can look but not change anything.</summary>
public abstract class AdminPage : PageModel
{
    public bool CanChange => User.HasClaim(PayLinkClaims.Access, "admin");

    protected Actor Me => User.ToActor(HttpContext);

    public override async Task OnPageHandlerExecutionAsync(PageHandlerExecutingContext context, PageHandlerExecutionDelegate next)
    {
        if (!HttpMethods.IsGet(Request.Method) && !CanChange)
        {
            TempData["Error"] = "Your access is read-only. Ask a platform admin to make this change.";
            context.Result = Redirect(Request.Path);
            return;
        }
        var repo = HttpContext.RequestServices.GetRequiredService<PlatformRepository>();
        ViewData["PendingAdmins"] = await repo.CountPendingAdminsAsync();
        ViewData["DockerHost"] = RuntimeEnvironment.DockerHostAddress();
        await next();
    }

    protected IActionResult Done(string message)
    {
        TempData["Ok"] = message;
        return Redirect(Request.Path + Request.QueryString);
    }

    protected IActionResult Failed(string message)
    {
        TempData["Error"] = message;
        return Redirect(Request.Path + Request.QueryString);
    }
}
