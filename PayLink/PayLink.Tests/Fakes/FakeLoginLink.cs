using System.Collections.Concurrent;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace PayLink.Tests.Fakes;

/// <summary>
/// A small in-process stand-in for a LoginLink server, implementing the endpoints PayLink uses with the shapes from
/// LoginLink's docs: per-organization discovery and JWKS, /authorize, /token (client_credentials and
/// authorization_code with PKCE), Management API apps and APIs, and the Platform API for creating organizations.
/// </summary>
public sealed class FakeLoginLink : IAsyncDisposable
{
    public sealed record Person(string Subject, string Email, string Name, string UserKind, string[] Roles);
    private sealed record Client(string Secret, string? Org, string Kind, string[] RedirectUris);
    private sealed record Code(string ClientId, string Org, string Nonce, string Challenge, string RedirectUri, Person Person);

    private readonly WebApplication _app;
    private readonly RsaSecurityKey _key = new(RSA.Create(2048)) { KeyId = "fake-1" };
    private readonly ConcurrentDictionary<string, Client> _clients = new();
    private readonly ConcurrentDictionary<string, Code> _codes = new();
    private readonly ConcurrentDictionary<string, Person> _nextSignIn = new();
    private readonly ConcurrentDictionary<string, Person> _ownersOfCreatedOrgs = new();
    private int _appIds;

    public string BaseUrl { get; }
    public ConcurrentDictionary<string, List<string>> RegisteredApis { get; } = new();
    public ConcurrentDictionary<string, JsonObject> AppPatches { get; } = new();
    /// <summary>Sign-in connectors switched on per app id. LoginLink starts a new app with none, and then refuses sign-in.</summary>
    public ConcurrentDictionary<string, HashSet<string>> AppConnectors { get; } = new();
    /// <summary>Connectors approved per organization (connector policy). Password is approved unless a test removes it.</summary>
    public ConcurrentDictionary<string, HashSet<string>> ApprovedConnectors { get; } = new();
    /// <summary>When false, management credentials lack ManageConnectors (403 on the connector endpoints).</summary>
    public bool ManageConnectorsAllowed { get; set; } = true;
    private readonly ConcurrentDictionary<string, string> _clientOfApp = new();

    private FakeLoginLink(WebApplication app, string baseUrl) { _app = app; BaseUrl = baseUrl; }

    public static async Task<FakeLoginLink> StartAsync()
    {
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();

        var url = $"http://127.0.0.1:{port}";
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls(url);
        builder.Logging.ClearProviders();
        var app = builder.Build();
        var fake = new FakeLoginLink(app, url);
        fake.Map(app);
        await app.StartAsync();
        return fake;
    }

    private void Map(WebApplication app)
    {
        app.MapGet("/.well-known/openid-configuration", () => Discovery(null));
        app.MapGet("/{org}/.well-known/openid-configuration", (string org) => Discovery(org));
        app.MapGet("/.well-known/jwks.json", Jwks);
        app.MapGet("/{org}/.well-known/jwks.json", (string org) => Jwks());
        app.MapGet("/{org}/authorize", (string org, HttpRequest r) => Authorize(org, r));
        app.MapGet("/authorize", (HttpRequest r) => Authorize("", r));
        app.MapPost("/{org}/token", (string org, HttpRequest r) => Token(org, r));
        app.MapPost("/token", (HttpRequest r) => Token("", r));
        app.MapPost("/api/v1/apps", RegisterApp);
        app.MapMethods("/api/v1/apps/{id}", ["PATCH"], PatchApp);
        app.MapGet("/api/v1/apps/{id}/connectors", AppConnectorList);
        app.MapMethods("/api/v1/apps/{id}/connectors", ["PATCH"], PatchAppConnector);
        app.MapGet("/api/v1/connector-policy", ConnectorPolicy);
        app.MapMethods("/api/v1/connector-policy/{name}", ["PATCH"], PatchConnectorPolicy);
        app.MapGet("/api/v1/apis", ListApis);
        app.MapPost("/api/v1/apis", AddApi);
        app.MapPost("/api/v1/tenants", CreateTenant);
    }

    private string Issuer(string? org) => string.IsNullOrEmpty(org) ? BaseUrl : $"{BaseUrl}/{org}";

    // ---------- test controls ----------

    /// <summary>A management credential (RegisterApps, ManageApis) for one organization.</summary>
    public (string ClientId, string Secret) AddManagementCredential(string org)
    {
        var id = "mgmt_" + org + "_" + Guid.NewGuid().ToString("N")[..6];
        var secret = "secret-" + Guid.NewGuid().ToString("N");
        _clients[id] = new Client(secret, org, "mgmt", []);
        return (id, secret);
    }

    /// <summary>A Platform API credential (CreateTenants).</summary>
    public (string ClientId, string Secret) AddPlatformCredential()
    {
        var id = "plat_" + Guid.NewGuid().ToString("N")[..6];
        var secret = "secret-" + Guid.NewGuid().ToString("N");
        _clients[id] = new Client(secret, null, "platform", []);
        return (id, secret);
    }

    /// <summary>The next sign-in to this organization completes as this person (no login form in the fake).</summary>
    public void NextSignIn(string org, Person person) => _nextSignIn[org] = person;

    // ---------- endpoints ----------

    private IResult Discovery(string? org)
    {
        var iss = Issuer(org);
        return Results.Json(new Dictionary<string, object>
        {
            ["issuer"] = iss,
            ["authorization_endpoint"] = iss + "/authorize",
            ["token_endpoint"] = iss + "/token",
            ["jwks_uri"] = iss + "/.well-known/jwks.json",
            ["response_types_supported"] = new[] { "code" },
            ["subject_types_supported"] = new[] { "public" },
            ["id_token_signing_alg_values_supported"] = new[] { "RS256" },
        });
    }

    private IResult Jwks()
    {
        var jwk = JsonWebKeyConverter.ConvertFromRSASecurityKey(_key);
        return Results.Json(new { keys = new[] { new { kty = "RSA", use = "sig", alg = "RS256", kid = jwk.Kid, n = jwk.N, e = jwk.E } } });
    }

    private IResult Authorize(string org, HttpRequest r)
    {
        var q = r.Query;
        var clientId = q["client_id"].ToString();
        if (!_clients.TryGetValue(clientId, out var client) || client.Kind != "app" || client.Org != org)
            return Results.BadRequest(new { error = "unauthorized_client" });
        var redirect = q["redirect_uri"].ToString();
        if (!client.RedirectUris.Contains(redirect)) return Results.BadRequest(new { error = "invalid_redirect_uri" });
        if (q["code_challenge_method"] != "S256" || string.IsNullOrEmpty(q["code_challenge"])) return Results.BadRequest(new { error = "pkce_required" });
        var appId = _clientOfApp.FirstOrDefault(kv => kv.Value == clientId).Key;
        if (appId is not null && AppConnectors.GetValueOrDefault(appId) is not { Count: > 0 })
            return Results.Content("This account has no sign-in method available for this app.", "text/plain", statusCode: 400);
        if (!_nextSignIn.TryRemove(org, out var person))
            return Results.Redirect($"{redirect}?error=access_denied&error_description=No+test+user&state={Uri.EscapeDataString(q["state"]!)}");

        var code = Guid.NewGuid().ToString("N");
        _codes[code] = new Code(clientId, org, q["nonce"]!, q["code_challenge"]!, redirect, person);
        return Results.Redirect($"{redirect}?code={code}&state={Uri.EscapeDataString(q["state"]!)}");
    }

    private async Task<IResult> Token(string org, HttpRequest r)
    {
        var form = await r.ReadFormAsync();
        var clientId = form["client_id"].ToString();
        if (!_clients.TryGetValue(clientId, out var client) || client.Secret != form["client_secret"])
            return Results.Json(new { error = "invalid_client", error_description = "Unknown client or wrong secret." }, statusCode: 401);

        if (form["grant_type"] == "client_credentials")
        {
            var token = Sign(Issuer(org), clientId, [new("client_id", clientId), new("cred_kind", client.Kind), new("org", client.Org ?? "")]);
            return Results.Json(new { access_token = token, token_type = "Bearer", expires_in = 3600 });
        }

        if (form["grant_type"] != "authorization_code" || !_codes.TryRemove(form["code"].ToString(), out var code) || code.ClientId != clientId)
            return Results.Json(new { error = "invalid_grant" }, statusCode: 400);
        var challenge = Base64UrlEncoder.Encode(SHA256.HashData(Encoding.ASCII.GetBytes(form["code_verifier"].ToString())));
        if (challenge != code.Challenge || form["redirect_uri"] != code.RedirectUri)
            return Results.Json(new { error = "invalid_grant", error_description = "PKCE or redirect mismatch" }, statusCode: 400);

        var iss = Issuer(code.Org);
        var p = code.Person;
        var id = Sign(iss, clientId, [new("sub", p.Subject), new("email", p.Email), new("name", p.Name), new("nonce", code.Nonce)]);
        var accessClaims = new List<Claim> { new("sub", p.Subject), new("user_kind", p.UserKind), new("org", code.Org), new("email", p.Email) };
        accessClaims.AddRange(p.Roles.Select(role => new Claim("roles", role)));
        var access = Sign(iss, clientId, accessClaims);
        return Results.Json(new { access_token = access, id_token = id, token_type = "Bearer", expires_in = 3600 });
    }

    private string Sign(string issuer, string audience, List<Claim> claims) =>
        new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = issuer,
            Audience = audience,
            Subject = new ClaimsIdentity(claims),
            Expires = DateTime.UtcNow.AddMinutes(10),
            SigningCredentials = new SigningCredentials(_key, SecurityAlgorithms.RsaSha256),
        });

    private Client? Caller(HttpRequest r, string kind)
    {
        var header = r.Headers.Authorization.ToString();
        if (!header.StartsWith("Bearer ")) return null;
        var jwt = new JsonWebToken(header[7..]);
        var clientId = jwt.GetClaim("client_id").Value;
        return _clients.TryGetValue(clientId, out var c) && c.Kind == kind ? c : null;
    }

    private async Task<IResult> RegisterApp(HttpRequest r)
    {
        var caller = Caller(r, "mgmt");
        if (caller is null) return Results.Json(new { error = "insufficient_scope" }, statusCode: 403);
        var body = (await JsonNode.ParseAsync(r.Body))!;
        var redirects = body["redirectUris"]!.AsArray().Select(x => (string)x!).ToArray();
        var appId = Interlocked.Increment(ref _appIds);
        var clientId = "app_" + Guid.NewGuid().ToString("N")[..10];
        var secret = "appsecret-" + Guid.NewGuid().ToString("N");
        _clients[clientId] = new Client(secret, caller.Org, "app", redirects);
        _clientOfApp[appId.ToString()] = clientId;
        return Results.Json(new { appId, clientId, clientSecret = secret }, statusCode: 201);
    }

    private async Task<IResult> PatchApp(string id, HttpRequest r)
    {
        if (Caller(r, "mgmt") is null) return Results.Json(new { error = "insufficient_scope" }, statusCode: 403);
        AppPatches[id] = (await JsonNode.ParseAsync(r.Body))!.AsObject();
        return Results.Json(new { appId = int.Parse(id) });
    }

    // An organization created with an owner password: assumed to come with Password on for its first app.
    private int NewPlatformApp(string clientId)
    {
        var id = Interlocked.Increment(ref _appIds);
        _clientOfApp[id.ToString()] = clientId;
        AppConnectors[id.ToString()] = ["Password"];
        return id;
    }

    private string? OrgOf(string appId) => _clientOfApp.TryGetValue(appId, out var c) && _clients.TryGetValue(c, out var client) ? client.Org : null;
    private HashSet<string> Approved(string org) => ApprovedConnectors.GetOrAdd(org, _ => ["Password"]);

    private IResult AppConnectorList(string id, HttpRequest r)
    {
        if (Caller(r, "mgmt") is null || !ManageConnectorsAllowed) return Results.Json(new { error = "insufficient_scope" }, statusCode: 403);
        var on = AppConnectors.GetValueOrDefault(id) ?? [];
        return Results.Json(Approved(OrgOf(id) ?? "").Select(n => new { name = n, enabled = on.Contains(n) }));
    }

    private async Task<IResult> PatchAppConnector(string id, HttpRequest r)
    {
        if (Caller(r, "mgmt") is null || !ManageConnectorsAllowed) return Results.Json(new { error = "insufficient_scope" }, statusCode: 403);
        var body = (await JsonNode.ParseAsync(r.Body))!;
        var name = (string)body["name"]!;
        if (!Approved(OrgOf(id) ?? "").Contains(name)) return Results.Json(new { error = "connector_not_approved" }, statusCode: 400);
        var set = AppConnectors.GetOrAdd(id, _ => []);
        if ((bool)body["enabled"]!) set.Add(name); else set.Remove(name);
        return Results.Json(new { name, enabled = set.Contains(name) });
    }

    private IResult ConnectorPolicy(HttpRequest r)
    {
        var caller = Caller(r, "mgmt");
        if (caller is null || !ManageConnectorsAllowed) return Results.Json(new { error = "insufficient_scope" }, statusCode: 403);
        var approved = Approved(caller.Org ?? "");
        return Results.Json(new { connectors = new[] { "Password", "MagicLink", "EmailOtp" }.Select(n => new { name = n, approved = approved.Contains(n), scopeMode = "all", appIds = Array.Empty<int>() }) });
    }

    private async Task<IResult> PatchConnectorPolicy(string name, HttpRequest r)
    {
        var caller = Caller(r, "mgmt");
        if (caller is null || !ManageConnectorsAllowed) return Results.Json(new { error = "insufficient_scope" }, statusCode: 403);
        var body = (await JsonNode.ParseAsync(r.Body))!;
        if ((bool?)body["approved"] == true) Approved(caller.Org ?? "").Add(name); else Approved(caller.Org ?? "").Remove(name);
        return Results.Json(new { name });
    }

    private IResult ListApis(HttpRequest r)
    {
        var caller = Caller(r, "mgmt");
        if (caller is null) return Results.Json(new { error = "insufficient_scope" }, statusCode: 403);
        var list = RegisteredApis.GetValueOrDefault(caller.Org!) ?? [];
        return Results.Json(new { apis = list.Select((identifier, i) => new { id = i + 1, name = "PayLink", identifier }) });
    }

    private async Task<IResult> AddApi(HttpRequest r)
    {
        var caller = Caller(r, "mgmt");
        if (caller is null) return Results.Json(new { error = "insufficient_scope" }, statusCode: 403);
        var body = (await JsonNode.ParseAsync(r.Body))!;
        RegisteredApis.GetOrAdd(caller.Org!, _ => []).Add((string)body["identifier"]!);
        return Results.Json(new { id = 1 }, statusCode: 201);
    }

    private async Task<IResult> CreateTenant(HttpRequest r)
    {
        if (Caller(r, "platform") is null) return Results.Json(new { error = "insufficient_scope" }, statusCode: 403);
        var body = (await JsonNode.ParseAsync(r.Body))!;
        var slug = new string(((string)body["tenantName"]!).ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());
        var redirects = body["app"]!["redirectUris"]!.AsArray().Select(x => (string)x!).ToArray();
        var clientId = "app_" + Guid.NewGuid().ToString("N")[..10];
        var secret = "appsecret-" + Guid.NewGuid().ToString("N");
        _clients[clientId] = new Client(secret, slug, "app", redirects);
        var email = (string)body["adminEmail"]!;
        _ownersOfCreatedOrgs[slug] = new Person("owner-" + slug, email, (string?)body["adminName"] ?? email, "tenant_admin", []);
        return Results.Json(new
        {
            tenantId = 99, slug, adminUserId = 1,
            app = new { appId = NewPlatformApp(clientId), clientId, clientSecret = secret },
            issuer = Issuer(slug), discoveryUrl = Issuer(slug) + "/.well-known/openid-configuration",
        }, statusCode: 201);
    }

    /// <summary>The owner LoginLink would create for an organization made through the Platform API.</summary>
    public Person OwnerOf(string org) => _ownersOfCreatedOrgs[org];

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}
