using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PayLink.Identity;

namespace PayLink.Pages.Console;

public sealed class LogoutModel : PageModel
{
    public IActionResult OnGet(string slug) => Redirect($"/{slug}/login");

    public async Task<IActionResult> OnPostAsync(string slug)
    {
        await HttpContext.SignOutAsync(AuthSchemes.Console);
        return Redirect($"/{slug}/login");
    }
}
