using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PayLink.Identity;
using PayLink.Infrastructure;
using PayLink.Platform;
using PayLink.Pages.Signup;
using PayLink.Tenancy;

namespace PayLink.Pages.Auth;

/// <summary>Every LoginLink sign-in returns here; what happens next depends on what the sign-in was for.</summary>
public sealed class CallbackModel(
    OidcSignIn oidc,
    BootstrapStore bootstrap,
    PlatformSettings settings,
    PlatformRepository platform,
    PlatformDb platformDb,
    AdmissionService admission,
    SecretProtector secrets,
    SignupSessions signups,
    TenantDb tenantDb,
    IDataProtectionProvider dataProtection,
    ILogger<CallbackModel> logger) : PageModel
{
    public string Title { get; private set; } = "Signing you in";
    public string? Message { get; private set; }
    public string? BackUrl { get; private set; }

    public async Task<IActionResult> OnGetAsync(string? code, string? state, string? error, string? error_description)
    {
        var pending = oidc.ReadPending(HttpContext);
        oidc.ClearPending(HttpContext);
        if (pending is null)
            return Fail("Your sign-in expired or was started in another browser. Please start again.", "/");
        if (error is not null)
            return Fail($"LoginLink didn't sign you in: {error_description ?? error}", pending.ReturnUrl ?? "/");

        try
        {
            if (pending.Purpose == "setup-admin") return await SetupAdminAsync(pending, code, state);
            if (pending.Purpose == "admin") return await AdminAsync(pending, code, state);
            if (pending.Purpose.StartsWith("tenant:", StringComparison.Ordinal)) return await TenantAsync(pending, pending.Purpose[7..], code, state);
            if (pending.Purpose.StartsWith("signup:", StringComparison.Ordinal)) return await SignupAsync(pending, pending.Purpose[7..], code, state);
            return Fail("Unknown sign-in purpose.", "/");
        }
        catch (LoginLinkException ex)
        {
            logger.LogWarning(ex, "Sign-in for {Purpose} failed", pending.Purpose);
            return Fail(ex.Message, pending.ReturnUrl ?? "/");
        }
    }

    private async Task<IActionResult> SetupAdminAsync(OidcPending pending, string? code, string? state)
    {
        if (await bootstrap.IsSetupCompleteAsync()) return Redirect("/admin");
        // The browser finishing setup must be the one that unlocked it with the setup code.
        var setupCode = await bootstrap.GetOrCreateSetupCodeAsync();
        var unlocked = Request.Cookies.TryGetValue(Setup.IndexModel.UnlockCookie, out var raw)
            && TryUnprotect(dataProtection.CreateProtector("PayLink.Setup.v1"), raw) == setupCode;
        if (!unlocked) return Fail("Finish setup in the same browser you entered the setup code in.", "/setup");

        var ll = await settings.GetLoginLinkAsync() ?? throw new LoginLinkException("LoginLink isn't connected.");
        var person = await oidc.CompleteAsync(HttpContext, pending, code, state, ll.AdminClientSecret);
        var now = Clock.Now();
        await platform.UpsertAdminAsync(new PlatformAdmin
        {
            Subject = person.Subject, Email = person.Email, Name = person.Name, Access = "admin", Status = "approved",
            Source = "setup", CreatedAt = now, DecidedBy = "setup", DecidedAt = now, LastSignInAt = now,
        });
        await bootstrap.MarkSetupCompleteAsync();
        await platformDb.AuditAsync(Actor.Person(person.Subject, person.Display, HttpContext.Connection.RemoteIpAddress?.ToString()), "setup.completed", person.Display, "first platform admin");
        Response.Cookies.Delete(Setup.IndexModel.UnlockCookie);
        await SignInHelper.SignInAdminAsync(HttpContext, person, "admin");
        TempData["Ok"] = "Setup is complete. Welcome to PayLink.";
        return Redirect("/admin");
    }

    private async Task<IActionResult> AdminAsync(OidcPending pending, string? code, string? state)
    {
        var ll = await settings.GetLoginLinkAsync() ?? throw new LoginLinkException("LoginLink isn't connected.");
        var person = await oidc.CompleteAsync(HttpContext, pending, code, state, ll.AdminClientSecret);
        var result = await admission.AdmitPlatformAsync(person);
        if (!result.Allowed) return Fail(result.Message, "/", "Waiting for approval");
        await SignInHelper.SignInAdminAsync(HttpContext, person, result.Grant!);
        await platformDb.AuditAsync(Actor.Person(person.Subject, person.Display, HttpContext.Connection.RemoteIpAddress?.ToString()), "admin.signed_in", person.Display, result.Grant);
        return LocalRedirect(pending.ReturnUrl is { } r && Url.IsLocalUrl(r) ? r : "/admin");
    }

    private async Task<IActionResult> TenantAsync(OidcPending pending, string slug, string? code, string? state)
    {
        var tenant = await platform.TenantBySlugAsync(slug);
        if (tenant is null) return Fail("That PayLink account doesn't exist.", "/");
        var person = await oidc.CompleteAsync(HttpContext, pending, code, state, secrets.Unprotect(tenant.ConsoleClientSecretEnc));
        var result = await admission.AdmitTenantAsync(tenant, person);
        if (!result.Allowed) return Fail(result.Message, $"/{slug}/login", "Waiting for approval");
        await SignInHelper.SignInConsoleAsync(HttpContext, person, slug, result.Grant!);
        await tenantDb.AuditAsync(tenant, Actor.Person(person.Subject, person.Display, HttpContext.Connection.RemoteIpAddress?.ToString()), "member.signed_in", person.Display, result.Grant);
        return LocalRedirect($"/{slug}/console");
    }

    private async Task<IActionResult> SignupAsync(OidcPending pending, string sessionId, string? code, string? state)
    {
        var session = await signups.LoadAsync(sessionId);
        if (session?.Org is null) return Fail("Your signup expired. Please start again.", "/signup");
        var person = await oidc.CompleteAsync(HttpContext, pending, code, state, session.Org.ClientSecret);

        if (person.Org is { } org && !string.Equals(org, session.Org.OrgSlug, StringComparison.OrdinalIgnoreCase))
            return Fail($"You signed in to the LoginLink organization \"{org}\", not \"{session.Org.OrgSlug}\".", "/signup");
        if (session.Org.Mode == "existing" && !person.IsTenantAdmin)
            return Fail($"Only an admin of the LoginLink organization \"{session.Org.OrgSlug}\" can connect it to PayLink.", "/signup");

        session.Subject = person.Subject;
        session.Email = person.Email;
        session.PersonName = person.Name;
        await signups.SaveAsync(sessionId, session);
        return Redirect("/signup");
    }

    private static string? TryUnprotect(IDataProtector protector, string? raw)
    {
        try { return raw is null ? null : protector.Unprotect(raw); }
        catch (System.Security.Cryptography.CryptographicException) { return null; }
    }

    private PageResult Fail(string message, string back, string title = "Sign-in didn't finish")
    {
        Title = title;
        Message = message;
        BackUrl = back;
        return Page();
    }
}
