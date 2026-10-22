using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PayLink.Payments;
using PayLink.Platform;

namespace PayLink.Pages;

/// <summary>The front page: what PayLink is and why, how to start with PayLink UPI, gateways with onboarding help, and the way in.</summary>
public sealed class IndexModel(PlatformSettings settings, PlatformRepository repo, ConnectorRegistry registry, ConnectorCatalog catalog) : PageModel
{
    public bool SignupOpen { get; private set; }
    public string? Error { get; private set; }
    /// <summary>Gateways this platform offers (installed and allowed in its catalog), by name only.</summary>
    public IReadOnlyList<string> Gateways { get; private set; } = [];

    private async Task LoadAsync()
    {
        SignupOpen = await settings.IsSignupOpenAsync();
        var names = new List<string>();
        foreach (var c in registry.All.Where(c => c.Manifest.Id is not ("upi" or "simulator")))
            if ((await catalog.GetAsync(c.Manifest.Id)).Allows(true)) names.Add(c.Manifest.Name);
        Gateways = names;
    }

    public async Task OnGetAsync() => await LoadAsync();

    /// <summary>"Sign in to my console": find the tenant by its address and go to its sign-in page.</summary>
    public async Task<IActionResult> OnPostAsync(string? slug)
    {
        await LoadAsync();
        slug = (slug ?? "").Trim().Trim('/').ToLowerInvariant();
        if (slug.Length > 0 && await repo.TenantBySlugAsync(slug) is not null) return Redirect($"/{slug}/login");
        Error = "No PayLink account has that address.";
        return Page();
    }
}
