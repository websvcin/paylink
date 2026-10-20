using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using PayLink.Infrastructure;

namespace PayLink.Identity;

/// <summary>What a sign-in round trip is for, carried in a protected cookie across the redirect to LoginLink.</summary>
public sealed class OidcPending
{
    public string State { get; set; } = "";
    public string Nonce { get; set; } = "";
    public string CodeVerifier { get; set; } = "";
    /// <summary>"setup-admin", "admin", "tenant:{slug}" or "signup:{sessionId}".</summary>
    public string Purpose { get; set; } = "";
    public string ServerUrl { get; set; } = "";
    public string DiscoveryUrl { get; set; } = "";
    public string ClientId { get; set; } = "";
    public string RedirectUri { get; set; } = "";
    public string? ReturnUrl { get; set; }
    public string CreatedAt { get; set; } = "";
}

/// <summary>The person LoginLink signed in, with the claims PayLink's admission rules need.</summary>
public sealed record SignedInPerson(
    string Subject,
    string? Email,
    string? Name,
    string? UserKind,
    IReadOnlyList<string> Roles,
    string? Org,
    string Issuer)
{
    public bool IsTenantAdmin => string.Equals(UserKind, "tenant_admin", StringComparison.OrdinalIgnoreCase);
    public string Display => Email ?? Name ?? Subject;
}

/// <summary>
/// Authorization Code + PKCE against any LoginLink organization. Each organization has its own issuer and keys,
/// so the authority is chosen per sign-in rather than fixed at startup.
/// </summary>
public sealed class OidcSignIn(LoginLinkClient loginLink, IDataProtectionProvider dataProtection, ILogger<OidcSignIn> logger)
{
    private const string CookieName = "paylink.oidc";
    private readonly IDataProtector _protector = dataProtection.CreateProtector("PayLink.Oidc.v1");

    public async Task<string> BeginAsync(HttpContext http, string serverUrl, string discoveryUrl, string clientId, string redirectUri, string purpose, string? returnUrl = null)
    {
        var config = await loginLink.GetDiscoveryAsync(discoveryUrl);
        var pending = new OidcPending
        {
            State = Token(), Nonce = Token(), CodeVerifier = Token() + Token(), Purpose = purpose,
            ServerUrl = serverUrl, DiscoveryUrl = discoveryUrl, ClientId = clientId, RedirectUri = redirectUri,
            ReturnUrl = returnUrl, CreatedAt = Clock.Now(),
        };
        http.Response.Cookies.Append(CookieName, _protector.Protect(JsonSerializer.Serialize(pending)), new CookieOptions
        {
            HttpOnly = true,
            Secure = http.Request.IsHttps,
            SameSite = SameSiteMode.Lax,   // the return from LoginLink is a top-level GET, which Lax allows
            Path = "/auth",
            MaxAge = TimeSpan.FromMinutes(15),
        });

        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(pending.CodeVerifier)));
        var query = new Dictionary<string, string?>
        {
            ["client_id"] = clientId,
            ["redirect_uri"] = redirectUri,
            ["response_type"] = "code",
            ["scope"] = "openid profile email",
            ["state"] = pending.State,
            ["nonce"] = pending.Nonce,
            ["code_challenge"] = challenge,
            ["code_challenge_method"] = "S256",
        };
        return Microsoft.AspNetCore.WebUtilities.QueryHelpers.AddQueryString(config.AuthorizationEndpoint, query);
    }

    public OidcPending? ReadPending(HttpContext http)
    {
        if (!http.Request.Cookies.TryGetValue(CookieName, out var raw)) return null;
        try
        {
            return JsonSerializer.Deserialize<OidcPending>(_protector.Unprotect(raw));
        }
        catch (CryptographicException)
        {
            return null;
        }
    }

    public void ClearPending(HttpContext http) => http.Response.Cookies.Delete(CookieName, new CookieOptions { Path = "/auth" });

    public async Task<SignedInPerson> CompleteAsync(HttpContext http, OidcPending pending, string? code, string? state, string clientSecret)
    {
        if (string.IsNullOrEmpty(code) || !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(state ?? ""), Encoding.UTF8.GetBytes(pending.State)))
            throw new LoginLinkException("The sign-in response didn't match the request. Please start again.");

        var config = await loginLink.GetDiscoveryAsync(pending.DiscoveryUrl);
        var tokens = await loginLink.ExchangeCodeAsync(config.TokenEndpoint, pending.ClientId, clientSecret, code, pending.RedirectUri, pending.CodeVerifier);
        var idToken = (string?)tokens?["id_token"] ?? throw new LoginLinkException("LoginLink didn't return an ID token.");
        var accessToken = (string?)tokens?["access_token"];

        // An organization's tokens may carry its own issuer or the platform's base issuer (LoginLink's dual acceptance).
        var issuers = new[] { config.Issuer, LoginLinkClient.NormalizeServer(pending.ServerUrl) }.Distinct().ToArray();
        var parameters = new TokenValidationParameters
        {
            ValidIssuers = issuers,
            ValidAudience = pending.ClientId,
            IssuerSigningKeys = config.SigningKeys,
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromMinutes(2),
        };

        var handler = new JsonWebTokenHandler();
        var id = await handler.ValidateTokenAsync(idToken, parameters);
        if (!id.IsValid && id.Exception is SecurityTokenSignatureKeyNotFoundException)
        {
            loginLink.RefreshDiscovery(pending.DiscoveryUrl);
            config = await loginLink.GetDiscoveryAsync(pending.DiscoveryUrl);
            parameters.IssuerSigningKeys = config.SigningKeys;
            id = await handler.ValidateTokenAsync(idToken, parameters);
        }
        if (!id.IsValid)
            throw new LoginLinkException("LoginLink's ID token couldn't be verified: " + (id.Exception?.Message ?? "invalid token"));
        if (!string.Equals(id.ClaimsIdentity.FindFirst("nonce")?.Value, pending.Nonce, StringComparison.Ordinal))
            throw new LoginLinkException("The sign-in response was replayed or tampered with (nonce mismatch).");

        var claims = id.ClaimsIdentity.Claims.ToList();

        // Roles and user_kind are carried on the access token; merge them in when it validates.
        if (accessToken is not null && accessToken.Count(ch => ch == '.') == 2)
        {
            var access = await handler.ValidateTokenAsync(accessToken, parameters);
            if (access.IsValid) claims.AddRange(access.ClaimsIdentity.Claims);
            else logger.LogInformation("Access token not used for claims: {Reason}", access.Exception?.Message);
        }

        string? First(string type) => claims.FirstOrDefault(c => c.Type == type)?.Value;
        var roles = claims.Where(c => c.Type is "roles" or "role" or ClaimTypes.Role)
            .SelectMany(c => c.Value.StartsWith('[') ? JsonSerializer.Deserialize<string[]>(c.Value) ?? [] : [c.Value])
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        var subject = First("sub") ?? First(ClaimTypes.NameIdentifier) ?? throw new LoginLinkException("LoginLink's token has no subject.");
        return new SignedInPerson(subject, First("email"), First("name"), First("user_kind"), roles, First("org"), First("iss") ?? config.Issuer);
    }

    private static string Token() => Base64Url(RandomNumberGenerator.GetBytes(32));
    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
