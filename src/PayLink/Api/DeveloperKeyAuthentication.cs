using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Options;
using PayLink.Identity;
using PayLink.Platform;

namespace PayLink.Api;

/// <summary>Authenticates "Authorization: Bearer pdk_…" platform developer keys. Only a hash is stored; the key itself is shown once.</summary>
public sealed class DeveloperKeyHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    PlatformRepository platform)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string KeyIdClaim = "pdk_id";
    public const string KeyNameClaim = "pdk_name";
    public const string CapabilityClaim = "pdk_cap";
    public const string ReachClaim = "pdk_reach";

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var header = Request.Headers.Authorization.ToString();
        if (!header.StartsWith("Bearer pdk_", StringComparison.Ordinal)) return AuthenticateResult.NoResult();

        var key = await platform.DeveloperKeyBySecretAsync(header["Bearer ".Length..].Trim());
        if (key is null || !key.IsUsable) return AuthenticateResult.Fail("invalid_token");

        await platform.TouchDeveloperKeyAsync(key.Id);
        var claims = new List<Claim>
        {
            new(KeyIdClaim, key.Id.ToString()),
            new(KeyNameClaim, key.Name),
            new(ReachClaim, key.Reach),
        };
        claims.AddRange(key.CapabilityList.Select(c => new Claim(CapabilityClaim, c)));
        return AuthenticateResult.Success(new AuthenticationTicket(
            new ClaimsPrincipal(new ClaimsIdentity(claims, AuthSchemes.DeveloperKey)), AuthSchemes.DeveloperKey));
    }

    protected override async Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = 401;
        await Response.WriteAsJsonAsync(new { error = "invalid_token", error_description = "Send a valid platform developer key as Authorization: Bearer pdk_…" });
    }

    protected override async Task HandleForbiddenAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = 403;
        await Response.WriteAsJsonAsync(new { error = "insufficient_scope", error_description = "This key doesn't have the capability this endpoint needs." });
    }
}

/// <summary>Requires a capability on the calling developer key; a missing one returns 403 insufficient_scope.</summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = true)]
public sealed class RequireCapabilityAttribute(string capability) : Attribute, IAuthorizationFilter
{
    public void OnAuthorization(AuthorizationFilterContext context)
    {
        if (!context.HttpContext.User.HasClaim(DeveloperKeyHandler.CapabilityClaim, capability))
            context.Result = new ObjectResult(new
            {
                error = "insufficient_scope",
                error_description = $"This key doesn't have the '{capability}' capability.",
            }) { StatusCode = 403 };
    }
}
