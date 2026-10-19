using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PayLink.Platform;

namespace PayLink.Pages;

public sealed class IndexModel(PlatformSettings settings, PlatformRepository repo) : PageModel
{
    public bool SignupOpen { get; private set; }
    public string? Error { get; private set; }

    public async Task OnGetAsync() => SignupOpen = await settings.IsSignupOpenAsync();

    /// <summary>"Sign in to my console": find the tenant by its address and go to its sign-in page.</summary>
    public async Task<IActionResult> OnPostAsync(string? slug)
    {
        SignupOpen = await settings.IsSignupOpenAsync();
        slug = (slug ?? "").Trim().Trim('/').ToLowerInvariant();
        if (slug.Length > 0 && await repo.TenantBySlugAsync(slug) is not null) return Redirect($"/{slug}/login");
        Error = "No PayLink account has that address.";
        return Page();
    }
}
