using Microsoft.AspNetCore.Mvc;
using PayLink.Infrastructure;

namespace PayLink.Pages.Admin;

/// <summary>Optional location data for risk rules: a country database (.mmdb) and Tor's exit list.</summary>
[RequestSizeLimit(200 * 1024 * 1024)]
[RequestFormLimits(MultipartBodyLengthLimit = 200 * 1024 * 1024)]
public sealed class LocationModel(GeoLocator geo, IHttpClientFactory httpFactory, PayLink.Platform.PlatformDb platformDb) : AdminPage
{
    public (bool Countries, string? Database, DateTime? Built, int TorExits, DateTime? TorUpdated) Status { get; private set; }
    public string? Lookup { get; private set; }
    [BindProperty(SupportsGet = true)] public string? Ip { get; set; }

    public void OnGet()
    {
        Status = geo.Status();
        if (!string.IsNullOrWhiteSpace(Ip)) Lookup = $"{Ip}: {geo.Country(Ip.Trim()) ?? "unknown country"}{(geo.IsTor(Ip.Trim()) ? ", Tor exit" : "")}";
    }

    public async Task<IActionResult> OnPostUploadAsync(IFormFile? database)
    {
        if (database is null || database.Length == 0) return Failed("Choose a .mmdb file.");
        await using var s = database.OpenReadStream();
        if (geo.Install(s) is { } problem) return Failed(problem);
        await platformDb.AuditAsync(Me, "location.database_installed", database.FileName);
        return Done("Location database installed. Country rules and the risk score use it from now on.");
    }

    public async Task<IActionResult> OnPostTorAsync()
    {
        try
        {
            var message = await geo.RefreshTorAsync(httpFactory.CreateClient("geo"), HttpContext.RequestAborted);
            await platformDb.AuditAsync(Me, "location.tor_refreshed", message);
            return Done(message);
        }
        catch (HttpRequestException ex) { return Failed("Couldn't download Tor's list: " + ex.Message); }
    }
}
