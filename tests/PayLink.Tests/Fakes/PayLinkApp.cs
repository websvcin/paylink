using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PayLink.Infrastructure;

namespace PayLink.Tests.Fakes;

/// <summary>PayLink running in-process with its own temporary data folder.</summary>
public sealed class PayLinkApp(string? dataRoot = null, bool keepData = false) : WebApplicationFactory<Program>
{
    public string DataRoot { get; } = dataRoot ?? Path.Combine(Path.GetTempPath(), "paylink-test-" + Guid.NewGuid().ToString("N"));

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("PayLink:DataRoot", DataRoot);
        builder.UseEnvironment("Development");
        builder.ConfigureLogging(l => l.SetMinimumLevel(LogLevel.Warning));
    }

    public T Get<T>() where T : notnull => Services.GetRequiredService<T>();

    public Task<string> SetupCodeAsync() => Get<BootstrapStore>().GetOrCreateSetupCodeAsync();

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (keepData) return;
        try { Directory.Delete(DataRoot, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}

/// <summary>
/// A cookie-keeping client that behaves like a browser across PayLink and the fake LoginLink:
/// it follows redirects between the two and sends antiforgery tokens with form posts.
/// </summary>
public sealed partial class Browser(PayLinkApp app)
{
    private readonly HttpClient _paylink = app.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true });
    private readonly HttpClient _outside = new(new HttpClientHandler { AllowAutoRedirect = false });

    public Uri? LastUrl { get; private set; }

    public async Task<HttpResponseMessage> GetAsync(string url)
    {
        LastUrl = new Uri(_paylink.BaseAddress!, url);
        return await _paylink.GetAsync(url);
    }

    /// <summary>GETs the page, takes its antiforgery token, and posts the form to ?handler=…</summary>
    public async Task<HttpResponseMessage> SubmitAsync(string pageUrl, string? handler, IDictionary<string, string?> fields)
    {
        var page = await GetAsync(pageUrl);
        var html = await page.Content.ReadAsStringAsync();
        var token = TokenPattern().Match(html);
        if (!token.Success) throw new InvalidOperationException($"No antiforgery token on {pageUrl} ({(int)page.StatusCode}): {Trim(html)}");

        var form = fields.Where(kv => kv.Value is not null).ToDictionary(kv => kv.Key, kv => kv.Value!);
        form["__RequestVerificationToken"] = WebUtility.HtmlDecode(token.Groups[1].Value);
        var path = pageUrl.Split('?')[0];
        var query = pageUrl.Contains('?') ? pageUrl[(pageUrl.IndexOf('?') + 1)..] + "&" : "";
        var target = handler is null ? pageUrl : $"{path}?{query}handler={handler}";
        LastUrl = new Uri(_paylink.BaseAddress!, target);
        return await _paylink.PostAsync(target, new FormUrlEncodedContent(form));
    }

    /// <summary>Follows redirects (including to and from LoginLink) until a non-redirect response.</summary>
    public async Task<HttpResponseMessage> FollowAsync(HttpResponseMessage response, int max = 10)
    {
        for (var i = 0; i < max && (int)response.StatusCode is >= 300 and < 400; i++)
        {
            var location = response.Headers.Location!;
            if (!location.IsAbsoluteUri) location = new Uri(LastUrl ?? _paylink.BaseAddress!, location);
            LastUrl = location;
            response = location.Host == _paylink.BaseAddress!.Host
                ? await _paylink.GetAsync(location.PathAndQuery)
                : await _outside.GetAsync(location);
        }
        return response;
    }

    public async Task<string> TextAsync(HttpResponseMessage response) => await response.Content.ReadAsStringAsync();

    public HttpClient Api => app.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = false });

    [GeneratedRegex("name=\"__RequestVerificationToken\" type=\"hidden\" value=\"([^\"]+)\"")]
    private static partial Regex TokenPattern();

    private static string Trim(string s) => s.Length > 400 ? s[..400] : s;
}
