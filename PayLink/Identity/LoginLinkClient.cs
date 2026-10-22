using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;

namespace PayLink.Identity;

public sealed class LoginLinkException(string message) : Exception(message);

public sealed record RegisteredApp(string AppId, string ClientId, string ClientSecret)
{
    /// <summary>Set when PayLink couldn't turn on a sign-in method for the app: what the person should do in LoginLink.</summary>
    public string? SignInNote { get; init; }
}

public sealed record CreatedOrganization(string Slug, string Issuer, string DiscoveryUrl, RegisteredApp? App);

/// <summary>
/// Talks to a LoginLink server: discovery, client-credentials tokens, the Management API (apps, APIs)
/// and the Platform API (creating organizations). Endpoint shapes follow LoginLink's public docs.
/// </summary>
public sealed class LoginLinkClient(IHttpClientFactory httpFactory, IConfiguration configuration, ILogger<LoginLinkClient> logger)
{
    private readonly ConcurrentDictionary<string, ConfigurationManager<OpenIdConnectConfiguration>> _discovery = new();

    public static string NormalizeServer(string url) => url.Trim().TrimEnd('/');

    /// <summary>
    /// Which organization an access token belongs to: its "org" claim, else the slug in its issuer
    /// ({server}/{slug}; "" for the platform root). Null when the token isn't a readable JWT. For messages and checks only;
    /// the token itself is verified by LoginLink when it's used.
    /// </summary>
    public static string? TokenOrganization(string token, string serverUrl)
    {
        var parts = token.Split('.');
        if (parts.Length != 3) return null;
        try
        {
            var payload = JsonNode.Parse(Microsoft.IdentityModel.Tokens.Base64UrlEncoder.Decode(parts[1]));
            if ((string?)payload?["org"] is { } org) return org.ToLowerInvariant();
            if ((string?)payload?["iss"] is { } iss)
            {
                var server = NormalizeServer(serverUrl);
                if (string.Equals(iss.TrimEnd('/'), server, StringComparison.OrdinalIgnoreCase)) return "";
                if (iss.StartsWith(server + "/", StringComparison.OrdinalIgnoreCase)) return iss[(server.Length + 1)..].Trim('/').ToLowerInvariant();
            }
        }
        catch (Exception ex) when (ex is JsonException or FormatException or ArgumentException) { }
        return null;
    }

    /// <summary>An organization's own issuer is {server}/{slug}; an empty slug means the platform root organization.</summary>
    public static string DiscoveryUrl(string serverUrl, string? orgSlug) =>
        string.IsNullOrWhiteSpace(orgSlug)
            ? $"{NormalizeServer(serverUrl)}/.well-known/openid-configuration"
            : $"{NormalizeServer(serverUrl)}/{orgSlug.Trim()}/.well-known/openid-configuration";

    private HttpClient Http() => httpFactory.CreateClient("loginlink");

    /// <summary>Plain HTTP is accepted only for loopback addresses (local testing) unless PayLink:LoginLink:AllowHttp is true.</summary>
    private bool RequireHttps(string url)
    {
        if (configuration.GetValue<bool>("PayLink:LoginLink:AllowHttp")) return false;
        var uri = new Uri(url);
        return !uri.IsLoopback;
    }

    public async Task<OpenIdConnectConfiguration> GetDiscoveryAsync(string discoveryUrl, CancellationToken ct = default)
    {
        var manager = _discovery.GetOrAdd(discoveryUrl, url => new ConfigurationManager<OpenIdConnectConfiguration>(
            url, new OpenIdConnectConfigurationRetriever(), new HttpDocumentRetriever(Http()) { RequireHttps = RequireHttps(url) }));
        try
        {
            return await manager.GetConfigurationAsync(ct);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or HttpRequestException)
        {
            _discovery.TryRemove(discoveryUrl, out _);
            logger.LogWarning(ex, "LoginLink discovery failed for {Url}", discoveryUrl);
            throw new LoginLinkException($"Couldn't read LoginLink's sign-in settings at {discoveryUrl}. Check the server address and organization slug.");
        }
    }

    /// <summary>Forces fresh signing keys, e.g. after LoginLink rotates them.</summary>
    public void RefreshDiscovery(string discoveryUrl)
    {
        if (_discovery.TryGetValue(discoveryUrl, out var m)) m.RequestRefresh();
    }

    public async Task<string> ClientCredentialsTokenAsync(string tokenEndpoint, string clientId, string clientSecret, CancellationToken ct = default)
    {
        using var response = await Http().PostAsync(tokenEndpoint, new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials",
            ["client_id"] = clientId,
            ["client_secret"] = clientSecret,
        }), ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new LoginLinkException("LoginLink refused the credential: " + DescribeError(body, response.StatusCode));
        return (string?)JsonNode.Parse(body)?["access_token"] ?? throw new LoginLinkException("LoginLink returned no access token.");
    }

    public async Task<JsonNode?> ExchangeCodeAsync(string tokenEndpoint, string clientId, string clientSecret, string code, string redirectUri, string codeVerifier, CancellationToken ct = default)
    {
        using var response = await Http().PostAsync(tokenEndpoint, new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = redirectUri,
            ["client_id"] = clientId,
            ["client_secret"] = clientSecret,
            ["code_verifier"] = codeVerifier,
        }), ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new LoginLinkException("LoginLink didn't accept the sign-in: " + DescribeError(body, response.StatusCode));
        return JsonNode.Parse(body);
    }

    // ---------- Management API (credential scoped to one organization) ----------

    private async Task<JsonNode?> CallAsync(HttpMethod method, string url, string token, object? body, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body is not null) request.Content = JsonContent.Create(body);
        using var response = await Http().SendAsync(request, ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new LoginLinkException($"LoginLink answered {(int)response.StatusCode} for {method} {new Uri(url).AbsolutePath}: {DescribeError(text, response.StatusCode)}");
        return string.IsNullOrWhiteSpace(text) ? null : JsonNode.Parse(text);
    }

    /// <summary>
    /// Registers a confidential app (needs RegisterApps) and lets both tenant admins and end users sign in to it,
    /// since LoginLink turns tenant admins away from apps by default (cross-surface sign-in).
    /// </summary>
    public async Task<RegisteredApp> RegisterAppAsync(string serverUrl, string token, string name, string redirectUri, CancellationToken ct = default)
    {
        var created = await CallAsync(HttpMethod.Post, $"{NormalizeServer(serverUrl)}/api/v1/apps", token,
            new { name, redirectUris = new[] { redirectUri } }, ct);
        var app = new RegisteredApp(
            created?["appId"]?.ToString() ?? throw new LoginLinkException("LoginLink didn't return the new app's id."),
            (string?)created["clientId"] ?? throw new LoginLinkException("LoginLink didn't return a client ID."),
            (string?)created["clientSecret"] ?? throw new LoginLinkException("LoginLink didn't return a client secret."));

        await CallAsync(HttpMethod.Patch, $"{NormalizeServer(serverUrl)}/api/v1/apps/{app.AppId}", token,
            new { allowEndUserSignIn = true, allowTenantAdminSignIn = true }, ct);
        return app with { SignInNote = await EnsureSignInMethodAsync(serverUrl, token, app.AppId, ct) };
    }

    /// <summary>Sign-in connectors PayLink turns on for its apps, in order of preference, when none is on yet.</summary>
    public static readonly string[] PreferredConnectors = ["Password", "MagicLink", "EmailOtp", "WebAuthn"];

    /// <summary>
    /// A new LoginLink app has no sign-in method switched on, and people then see "has no sign-in method available for this app".
    /// This keeps one that's on, or turns on the first approved one (approving Password for this app if nothing is). Needs
    /// ManageConnectors; without it, returns what to do in LoginLink instead of failing.
    /// </summary>
    public async Task<string?> EnsureSignInMethodAsync(string serverUrl, string token, string appId, CancellationToken ct = default)
    {
        var server = NormalizeServer(serverUrl);
        try
        {
            var current = await CallAsync(HttpMethod.Get, $"{server}/api/v1/apps/{appId}/connectors", token, null, ct) as JsonArray ?? [];
            if (current.Any(c => (bool?)c?["enabled"] == true)) return null;

            var policy = (await CallAsync(HttpMethod.Get, $"{server}/api/v1/connector-policy", token, null, ct))?["connectors"] as JsonArray ?? [];
            bool AllowedHere(JsonNode? c) => (bool?)c?["approved"] == true
                && ((string?)c?["scopeMode"] != "specific" || (c?["appIds"] as JsonArray)?.Any(i => i?.ToString() == appId) == true);
            var pick = PreferredConnectors.FirstOrDefault(n => policy.Any(c => (string?)c?["name"] == n && AllowedHere(c)));
            if (pick is null)
            {
                pick = "Password";
                var password = policy.FirstOrDefault(c => (string?)c?["name"] == pick);
                if ((string?)password?["scopeMode"] == "specific")
                {
                    var ids = ((password?["appIds"] as JsonArray) ?? []).Select(i => i!.GetValue<int>()).Append(int.Parse(appId)).Distinct().ToArray();
                    await CallAsync(HttpMethod.Patch, $"{server}/api/v1/connector-policy/{pick}", token, new { approved = true, scopeMode = "specific", appIds = ids }, ct);
                }
                else await CallAsync(HttpMethod.Patch, $"{server}/api/v1/connector-policy/{pick}", token, new { approved = true }, ct);
            }
            await CallAsync(HttpMethod.Patch, $"{server}/api/v1/apps/{appId}/connectors", token, new { name = pick, enabled = true }, ct);
            return null;
        }
        catch (Exception ex) when (ex is LoginLinkException or FormatException or InvalidOperationException)
        {
            return "Turn on a sign-in method (for example Email & Password) for the app in LoginLink: Console → Apps → the app → Connectors. " +
                   "PayLink couldn't do it with this credential (it needs Manage connectors): " + ex.Message;
        }
    }

    /// <summary>Replaces an app's redirect URIs (needs Register apps / Manage apps), e.g. to add a tenant's subdomain callback.</summary>
    public async Task SetAppRedirectUrisAsync(string serverUrl, string token, string appId, IEnumerable<string> redirectUris, CancellationToken ct = default) =>
        await CallAsync(HttpMethod.Patch, $"{NormalizeServer(serverUrl)}/api/v1/apps/{appId}", token, new { redirectUris = redirectUris.Distinct().ToArray() }, ct);

    /// <summary>Registers PayLink as an API (resource server) so apps can request tokens addressed to it. Needs ManageApis.</summary>
    public async Task EnsureApiAsync(string serverUrl, string token, string identifier, CancellationToken ct = default)
    {
        var list = await CallAsync(HttpMethod.Get, $"{NormalizeServer(serverUrl)}/api/v1/apis", token, null, ct);
        if (list?["apis"] is JsonArray apis && apis.Any(a => string.Equals((string?)a?["identifier"], identifier, StringComparison.OrdinalIgnoreCase)))
            return;
        await CallAsync(HttpMethod.Post, $"{NormalizeServer(serverUrl)}/api/v1/apis", token,
            new { name = "PayLink", identifier, description = "PayLink payments API" }, ct);
    }

    // ---------- Platform API (platform credential with CreateTenants) ----------

    public async Task<CreatedOrganization> CreateOrganizationAsync(
        string serverUrl, string platformToken, string tenantName, string adminEmail, string? adminName, string password,
        string appName, string redirectUri, CancellationToken ct = default)
    {
        var result = await CallAsync(HttpMethod.Post, $"{NormalizeServer(serverUrl)}/api/v1/tenants", platformToken, new
        {
            tenantName, adminEmail, adminName, password,
            app = new { name = appName, redirectUris = new[] { redirectUri }, allowEndUserSignIn = true, allowTenantAdminSignIn = true },
        }, ct);
        var slug = (string?)result?["slug"] ?? throw new LoginLinkException("LoginLink didn't return the new organization's slug.");
        var app = result?["app"] is JsonObject a
            ? new RegisteredApp(a["appId"]?.ToString() ?? "", (string?)a["clientId"] ?? "", (string?)a["clientSecret"] ?? "")
            : null;
        return new CreatedOrganization(slug,
            (string?)result?["issuer"] ?? $"{NormalizeServer(serverUrl)}/{slug}",
            (string?)result?["discoveryUrl"] ?? DiscoveryUrl(serverUrl, slug),
            app);
    }

    private static string DescribeError(string body, System.Net.HttpStatusCode status)
    {
        try
        {
            var json = JsonNode.Parse(body);
            var error = (string?)json?["error"];
            var description = (string?)json?["error_description"];
            if (error is not null) return description is null ? error : $"{error}: {description}";
        }
        catch (JsonException) { }
        return $"HTTP {(int)status}";
    }
}
