using Microsoft.AspNetCore.Mvc;
using PayLink.Infrastructure;
using PayLink.Platform;
using WebSvc.PayLink.Storage.Provisioning;

namespace PayLink.Pages.Admin;

public sealed class HostingModel(BootstrapStore bootstrap, PlatformDb platformDb, IHttpClientFactory httpFactory) : AdminPage
{
    [BindProperty] public HostingInput Hosting { get; set; } = new();
    public HostingSettings Saved { get; private set; } = new();

    public async Task OnGetAsync()
    {
        Saved = await bootstrap.GetHostingAsync();
        Hosting = HostingInput.From(Saved);
    }

    public async Task<IActionResult> OnPostAsync(string action)
    {
        Saved = await bootstrap.GetHostingAsync();
        if (Hosting.Validate() is { } problem) return Failed(problem);
        var next = Hosting.ToSettings(Saved);

        if (action == "test")
        {
            var provisioner = DatabaseProvisioners.Create(next, httpFactory.CreateClient("hosting"));
            if (provisioner is null) return Failed("Choose a hosting environment to test.");
            var check = await provisioner.TestAsync();
            return check.Ok ? Done(check.Message) : Failed(check.Message);
        }

        await bootstrap.SetHostingAsync(next);
        await platformDb.AuditAsync(Me, "hosting.changed", next.Describe());
        return Done("Saved. New auto-provisioned databases use this hosting.");
    }
}
