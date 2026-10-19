using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;
using PayLink.Infrastructure;
using PayLink.Platform;
using PayLink.Tenancy;

namespace PayLink.Pages.Signup;

/// <summary>A tenant signup in progress. Survives the redirect to LoginLink and back.</summary>
public sealed class SignupSession
{
    public OrgConnection? Org { get; set; }
    public string? Subject { get; set; }
    public string? Email { get; set; }
    public string? PersonName { get; set; }
    public string? SuggestedName { get; set; }
    public string CreatedAt { get; set; } = Clock.Now();

    public bool Verified => Subject is not null;
}

/// <summary>Stores signup state encrypted in the platform database; the browser holds only a protected session id.</summary>
public sealed class SignupSessions(PlatformRepository platform, SecretProtector secrets, IDataProtectionProvider dataProtection, IHttpContextAccessor http)
{
    private const string CookieName = "paylink.signup";
    private static readonly TimeSpan Lifetime = TimeSpan.FromHours(2);
    private IDataProtector Protector => dataProtection.CreateProtector("PayLink.Signup.v1");

    public string? CurrentId()
    {
        var context = http.HttpContext!;
        if (!context.Request.Cookies.TryGetValue(CookieName, out var raw)) return null;
        try { return Protector.Unprotect(raw); }
        catch (CryptographicException) { return null; }
    }

    public async Task<(string Id, SignupSession Session)?> CurrentAsync()
    {
        var id = CurrentId();
        if (id is null) return null;
        var session = await LoadAsync(id);
        return session is null ? null : (id, session);
    }

    public async Task<SignupSession?> LoadAsync(string id) => secrets.UnprotectJson<SignupSession>(await platform.LoadSignupSessionAsync(id));

    public async Task<string> StartAsync(SignupSession session)
    {
        var id = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        await SaveAsync(id, session);
        var context = http.HttpContext!;
        context.Response.Cookies.Append(CookieName, Protector.Protect(id), new CookieOptions
        {
            HttpOnly = true, Secure = context.Request.IsHttps, SameSite = SameSiteMode.Lax, MaxAge = Lifetime,
        });
        return id;
    }

    public Task SaveAsync(string id, SignupSession session) => platform.SaveSignupSessionAsync(id, secrets.ProtectJson(session), Lifetime);

    public async Task EndAsync()
    {
        if (CurrentId() is { } id) await platform.DeleteSignupSessionAsync(id);
        http.HttpContext!.Response.Cookies.Delete(CookieName);
    }
}
