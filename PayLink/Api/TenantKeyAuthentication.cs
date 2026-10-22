using System.Diagnostics;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Options;
using PayLink.Payments;
using PayLink.Platform;

namespace PayLink.Api;

/// <summary>
/// Authenticates a tenant's keys: "Authorization: Bearer sk_test_…/sk_live_…" (Payments API) or "Bearer tdk_…" (tenant developer API).
/// The key names its tenant; the hash is checked in that tenant's own database.
/// </summary>
public sealed class TenantKeyHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    PlatformRepository platform,
    TenantKeyService keys)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string Scheme = "PayLinkTenantKey";
    public const string TenantItem = "paylink.tenant";
    public const string KeyItem = "paylink.key";
    public const string KindClaim = "tk_kind";
    public const string LiveClaim = "tk_live";
    public const string CapabilityClaim = "tk_cap";

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var header = Request.Headers.Authorization.ToString();
        if (!header.StartsWith("Bearer ", StringComparison.Ordinal)) return AuthenticateResult.NoResult();
        var secret = header["Bearer ".Length..].Trim();
        if (TenantKeyService.Parse(secret) is not { } parsed) return AuthenticateResult.NoResult();

        var tenant = await platform.TenantBySlugAsync(parsed.Slug);
        if (tenant is null) return AuthenticateResult.Fail("invalid_key");
        if (!tenant.IsActive) { Context.Items["paylink.auth_error"] = "tenant_suspended"; return AuthenticateResult.Fail("tenant_suspended"); }
        var key = await keys.FindAsync(tenant, secret);
        if (key is not { IsUsable: true }) return AuthenticateResult.Fail("invalid_key");

        Context.Items[TenantItem] = tenant;
        Context.Items[KeyItem] = key;
        var claims = new List<Claim> { new(KindClaim, key.Kind), new(LiveClaim, key.Livemode ? "true" : "false") };
        claims.AddRange(key.CapabilityList.Select(c => new Claim(CapabilityClaim, c)));
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity(claims, Scheme)), Scheme));
    }

    protected override async Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = 401;
        if (Context.Items["paylink.auth_error"] as string == "tenant_suspended")
        {
            Response.StatusCode = 403;
            await Response.WriteAsJsonAsync(new { error = "tenant_suspended", error_description = "This PayLink account is suspended." });
            return;
        }
        await Response.WriteAsJsonAsync(new { error = "invalid_key", error_description = "Send a valid key as Authorization: Bearer sk_test_… (Payments API) or tdk_… (tenant developer API)." });
    }

    protected override async Task HandleForbiddenAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = 403;
        await Response.WriteAsJsonAsync(new { error = "wrong_key_type", error_description = "This API needs a different kind of key: sk_ keys for /api/v1, tdk_ keys for /api/tenant." });
    }
}

/// <summary>Requires a capability on the calling tenant developer key; a missing one returns 403 insufficient_scope.</summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = true)]
public sealed class RequireTenantCapabilityAttribute(string capability) : Attribute, IAuthorizationFilter
{
    public string Capability => capability;
    public void OnAuthorization(AuthorizationFilterContext context)
    {
        if (!context.HttpContext.User.HasClaim(TenantKeyHandler.CapabilityClaim, capability))
            context.Result = new ObjectResult(new { error = "insufficient_scope", error_description = $"This key doesn't have the '{capability}' capability." }) { StatusCode = 403 };
    }
}

/// <summary>Records every call to /api/v1 and /api/tenant in the tenant's API request log (no bodies).</summary>
public sealed class ApiRequestLogging(RequestDelegate next, ILogger<ApiRequestLogging> logger)
{
    public const string ErrorItem = "paylink.api_error";

    public async Task InvokeAsync(HttpContext context, TenantKeyService keys)
    {
        var path = context.Request.Path.Value ?? "";
        if (!path.StartsWith("/api/v1/", StringComparison.OrdinalIgnoreCase) && !path.StartsWith("/api/tenant/", StringComparison.OrdinalIgnoreCase))
        {
            await next(context);
            return;
        }
        var watch = Stopwatch.StartNew();
        try { await next(context); }
        finally
        {
            if (context.Items[TenantKeyHandler.TenantItem] is TenantRecord tenant)
            {
                var key = context.Items[TenantKeyHandler.KeyItem] as TenantApiKey;
                try
                {
                    await keys.LogRequestAsync(tenant, new ApiRequestLog
                    {
                        At = Infrastructure.Clock.Now(), KeyId = key?.Id, KeyName = key?.Name, Livemode = key?.Kind == TenantKeyKinds.Secret ? key.Livemode : null,
                        Method = context.Request.Method, Path = path.Length <= 300 ? path : path[..300], Status = context.Response.StatusCode,
                        DurationMs = (int)watch.ElapsedMilliseconds, IdempotencyKey = Trim(context.Request.Headers["Idempotency-Key"].ToString(), 200),
                        Ip = context.Connection.RemoteIpAddress?.ToString(), Error = context.Items[ErrorItem] as string,
                    });
                }
                catch (Exception ex) { logger.LogWarning(ex, "Couldn't write the API request log for {Tenant}", tenant.Slug); }
            }
        }
    }

    private static string? Trim(string value, int max) => string.IsNullOrEmpty(value) ? null : value.Length <= max ? value : value[..max];
}
