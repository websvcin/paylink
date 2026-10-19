using Microsoft.AspNetCore.Mvc;
using PayLink.Identity;
using PayLink.Infrastructure;
using PayLink.Platform;

namespace PayLink.Pages.Admin;

/// <summary>Platform developer keys (pdk_). Created and revoked only here, from a signed-in session, so a key can never mint another key.</summary>
public sealed class DeveloperApiModel(PlatformRepository repo, PlatformDb platformDb) : AdminPage
{
    public IReadOnlyList<DeveloperKey> Keys { get; private set; } = [];

    [BindProperty] public string? Name { get; set; }
    [BindProperty] public List<string> Caps { get; set; } = [];
    [BindProperty] public string Reach { get; set; } = "all";
    [BindProperty] public int? CreationLimit { get; set; }
    [BindProperty] public DateTime? ExpiresOn { get; set; }

    public async Task OnGetAsync() => Keys = await repo.DeveloperKeysAsync();

    public async Task<IActionResult> OnPostCreateAsync()
    {
        if (string.IsNullOrWhiteSpace(Name)) return Failed("Name the key, e.g. \"Signup automation\".");
        var caps = Caps.Intersect(Capabilities.All.Select(c => c.Name)).ToList();
        if (caps.Count == 0) return Failed("Tick at least one capability.");
        if (Reach is not ("all" or "own")) Reach = "all";
        var expires = ExpiresOn is { } d ? Clock.Format(DateTime.SpecifyKind(d.Date.AddDays(1), DateTimeKind.Utc)) : null;

        var (key, secret) = await repo.CreateDeveloperKeyAsync(Name.Trim(), caps, Reach, CreationLimit, expires, User.Get(PayLinkClaims.Email));
        await platformDb.AuditAsync(Me, "developer_key.created", key.Name, $"{key.KeyPrefix}… · {key.Capabilities} · reach {key.Reach}");
        TempData["Secret"] = secret;
        return Done($"Key \"{key.Name}\" created.");
    }

    public async Task<IActionResult> OnPostRevokeAsync(long id)
    {
        await repo.RevokeDeveloperKeyAsync(id);
        await platformDb.AuditAsync(Me, "developer_key.revoked", id.ToString());
        return Done("Key revoked. It stops working immediately.");
    }
}
