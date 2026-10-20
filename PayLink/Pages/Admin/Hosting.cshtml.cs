using Microsoft.AspNetCore.Mvc;
using PayLink.Infrastructure;
using PayLink.Platform;
using WebSvc.PayLink.Storage.Provisioning;

namespace PayLink.Pages.Admin;

public sealed class HostingModel(BootstrapStore bootstrap, PlatformDb platformDb, IHttpClientFactory httpFactory) : AdminPage
{
    [BindProperty] public HostingInput Hosting { get; set; } = new();
    public HostingSettings Saved { get; private set; } = new();
    public bool DraftExists { get; private set; }
    /// <summary>The secrets the form can fall back to: the last tested settings, else the saved ones.</summary>
    public HostingSettings DraftOrSaved { get; private set; } = new();

    public async Task OnGetAsync()
    {
        Saved = await bootstrap.GetHostingAsync();
        var draft = await bootstrap.GetHostingDraftAsync();
        DraftExists = draft is not null;
        DraftOrSaved = draft ?? Saved;
        Hosting = HostingInput.From(DraftOrSaved);
    }

    public async Task<IActionResult> OnPostAsync(string action)
    {
        Saved = await bootstrap.GetHostingAsync();
        if (Hosting.Validate() is { } problem) return Failed(problem);
        // Password fields come back empty after a test; fall back to what was just tested, then to what was saved.
        var next = Hosting.ToSettings(await bootstrap.GetHostingDraftAsync() ?? Saved);

        if (action == "test")
        {
            var provisioner = DatabaseProvisioners.Create(next, httpFactory.CreateClient("hosting"));
            if (provisioner is null) return Failed("Choose a hosting environment to test.");
            var check = await provisioner.TestAsync();
            await bootstrap.SetHostingDraftAsync(next);
            return check.Ok ? Done(check.Message + " Not saved yet: press Save to use it.") : Failed(check.Message);
        }

        // Saving always tests first, so settings that can't create a database are never stored.
        if (DatabaseProvisioners.Create(next, httpFactory.CreateClient("hosting")) is { } p)
        {
            var check = await p.TestAsync();
            if (!check.Ok) { await bootstrap.SetHostingDraftAsync(next); return Failed("Not saved: " + check.Message); }
        }
        await bootstrap.SetHostingAsync(next);
        await platformDb.AuditAsync(Me, "hosting.changed", next.Describe());
        return Done(next.Kind == HostingKind.None ? "Auto-provisioning is off." : "Tested and saved. New auto-provisioned databases use this hosting.");
    }
}
