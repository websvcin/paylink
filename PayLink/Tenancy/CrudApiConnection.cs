using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dapper;
using MySqlConnector;
using PayLink.Infrastructure;
using PayLink.Platform;
using WebSvc.PayLink.Storage;
using WebSvc.PayLink.Storage.Provisioning;

namespace PayLink.Tenancy;

/// <summary>What PayLink keeps about a tenant's CrudApi connection (encrypted in tenant_settings).</summary>
public sealed class CrudApiLink
{
    public string ReadUser { get; set; } = "";
    public string ReadPassword { get; set; } = "";
    /// <summary>Set when PayLink created the read-only user, so disconnecting removes it.</summary>
    public string? CreatedUserHost { get; set; }
    /// <summary>The database host as CrudApi should reach it (often not the address PayLink uses).</summary>
    public string Host { get; set; } = "";
    public uint Port { get; set; } = 3306;
    public string Database { get; set; } = "";
    public string? Server { get; set; }
    public string? DeveloperKey { get; set; }
    public string? Workspace { get; set; }
    public string? RegistrationId { get; set; }
    public string? ReaderClientId { get; set; }
    public string? Note { get; set; }
    public string CreatedAt { get; set; } = "";
}

/// <summary>
/// CrudApi's workspace developer API, the few calls the "Connect CrudApi" journey needs: check the key, register a database, create a
/// Reader API client, unregister. The routes are CrudApi's documented ones; the JSON fields follow its console's "Register a database"
/// form, and any refusal is shown to the person as CrudApi worded it, with the manual steps next to it.
/// </summary>
public sealed class CrudApiClient(IHttpClientFactory httpFactory)
{
    public const string HttpClientName = "crudapi";

    private HttpRequestMessage Request(HttpMethod method, string server, string path, string key, object? body = null)
    {
        var r = new HttpRequestMessage(method, server.TrimEnd('/') + path);
        r.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        if (body is not null) r.Content = JsonContent.Create(body);
        return r;
    }

    private async Task<(bool Ok, JsonNode? Json, string Error)> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        try
        {
            using (request)
            using (var response = await httpFactory.CreateClient(HttpClientName).SendAsync(request, ct))
            {
                var text = await response.Content.ReadAsStringAsync(ct);
                JsonNode? json = null;
                try { json = string.IsNullOrWhiteSpace(text) ? null : JsonNode.Parse(text); } catch (JsonException) { }
                if (response.IsSuccessStatusCode) return (true, json, "");
                var code = (string?)json?["error"];
                return (false, json, code switch
                {
                    "invalid_key" => "CrudApi didn't accept the developer key.",
                    "insufficient_scope" => "The developer key needs the Databases and API credentials capabilities.",
                    _ => $"CrudApi answered {(int)response.StatusCode}{(code is null ? "" : $" ({code})")}" +
                         (((string?)json?["message"] ?? (string?)json?["detail"]) is { } m ? ": " + m : "."),
                });
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or UriFormatException or InvalidOperationException)
        {
            return (false, null, "PayLink couldn't reach CrudApi: " + ex.Message);
        }
    }

    public async Task<string?> CheckAsync(string server, string key, CancellationToken ct = default) =>
        (await SendAsync(Request(HttpMethod.Get, server, "/api/tenant/databases", key), ct)) is { Ok: false } r ? r.Error : null;

    public async Task<(string? Id, string? Error)> RegisterAsync(string server, string key, CrudApiLink link, string name, CancellationToken ct = default)
    {
        var (ok, json, error) = await SendAsync(Request(HttpMethod.Post, server, "/api/tenant/databases", key, new
        {
            name, engine = "mysql", host = link.Host, port = link.Port, database = link.Database, user = link.ReadUser, password = link.ReadPassword,
        }), ct);
        return ok ? (((string?)json?["id"]?.ToString()) ?? (string?)json?["key"] ?? name, null) : (null, error);
    }

    public async Task<(string? ClientId, string? Secret, string? Error)> CreateReaderAsync(string server, string key, string name, CancellationToken ct = default)
    {
        var (ok, json, error) = await SendAsync(Request(HttpMethod.Post, server, "/api/tenant/api-clients", key, new { name, role = "Reader" }), ct);
        return ok ? ((string?)json?["clientId"] ?? (string?)json?["id"]?.ToString(), (string?)json?["clientSecret"] ?? (string?)json?["secret"], null) : (null, null, error);
    }

    public async Task<string?> UnregisterAsync(string server, string key, string id, CancellationToken ct = default) =>
        (await SendAsync(Request(HttpMethod.Delete, server, "/api/tenant/databases/" + Uri.EscapeDataString(id), key), ct)) is { Ok: false } r ? r.Error : null;
}

/// <summary>
/// "Connect CrudApi": full, read-only table access to a tenant's own payment data through CrudApi. CrudApi reads MySQL, so the tenant
/// database must be MySQL. Read-only is enforced twice: by a database user that can only SELECT (checked by trying to write) and by
/// CrudApi's Reader role. Payments never depend on it.
/// </summary>
public sealed class CrudApiConnection(TenantDb tenantDb, SecretProtector secrets, BootstrapStore bootstrap, CrudApiClient crudApi, IHttpClientFactory httpFactory)
{
    private const string SettingKey = "crudapi";

    public async Task<CrudApiLink?> GetAsync(TenantRecord tenant) =>
        secrets.UnprotectJson<CrudApiLink>(await tenantDb.GetSettingAsync(tenant, SettingKey));

    private Task SaveAsync(TenantRecord tenant, CrudApiLink? link) =>
        tenantDb.SetSettingAsync(tenant, SettingKey, link is null ? "" : secrets.ProtectJson(link));

    /// <summary>Why CrudApi can't be connected for this tenant, or null.</summary>
    public string? Unavailable(TenantRecord tenant) => tenantDb.Target(tenant).Engine == DbEngine.MySql ? null
        : $"CrudApi reads MySQL databases, and {tenant.Name}'s data is on {(tenantDb.Target(tenant).Engine == DbEngine.Sqlite ? "SQLite" : "PostgreSQL")}. A platform admin can move it to MySQL (Admin → Tenants), with all its data.";

    /// <summary>PayLink can make the read-only user itself when it created the database on a MySQL server it administers.</summary>
    public async Task<bool> CanCreateUserAsync(TenantRecord tenant) =>
        tenant.DbMode == "auto" && (await bootstrap.GetHostingAsync()).Kind == HostingKind.MySqlServer;

    private MySqlConnectionStringBuilder Builder(TenantRecord tenant) => new(tenantDb.Target(tenant).ConnectionString);

    /// <summary>Checks a user can read the tenant's tables and can't change anything.</summary>
    public async Task<string?> CheckReadOnlyAsync(TenantRecord tenant, string user, string password)
    {
        var b = Builder(tenant);
        b.UserID = user;
        b.Password = password;
        try
        {
            await using var c = new MySqlConnection(b.ToString());
            await c.OpenAsync();
            await c.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM payments;");
            try
            {
                await c.ExecuteAsync("UPDATE payments SET status = status WHERE 1 = 0;");
                return $"{user} can change payments. Use a user with SELECT only, so nothing reached through CrudApi can alter a payment.";
            }
            catch (MySqlException ex) when (ex.ErrorCode is MySqlErrorCode.TableAccessDenied or MySqlErrorCode.ColumnAccessDenied or MySqlErrorCode.DatabaseAccessDenied)
            {
                return null;   // refused to write: that's what we want
            }
        }
        catch (MySqlException ex)
        {
            return $"{user} couldn't read the database: {ex.Message}";
        }
    }

    /// <summary>Step 1: a read-only user, made by PayLink or given by the tenant (and verified).</summary>
    public async Task<string?> SetReaderAsync(TenantRecord tenant, string? user, string? password, string? host, Actor actor)
    {
        if (Unavailable(tenant) is { } why) return why;
        var b = Builder(tenant);
        var link = new CrudApiLink { Host = string.IsNullOrWhiteSpace(host) ? b.Server : host.Trim(), Port = b.Port == 0 ? 3306 : b.Port, Database = b.Database, CreatedAt = Clock.Now() };
        if (string.IsNullOrWhiteSpace(user))
        {
            if (!await CanCreateUserAsync(tenant)) return "Enter a MySQL user that can only read this database (GRANT SELECT).";
            var provisioner = (MySqlServerProvisioner)DatabaseProvisioners.Create(await bootstrap.GetHostingAsync(), httpFactory.CreateClient("hosting"))!;
            try { (link.ReadUser, link.ReadPassword, link.CreatedUserHost) = await provisioner.CreateReadOnlyUserAsync(b.Database); }
            catch (ProvisioningException ex) { return ex.Message; }
        }
        else
        {
            link.ReadUser = user.Trim();
            link.ReadPassword = password ?? "";
        }
        if (await CheckReadOnlyAsync(tenant, link.ReadUser, link.ReadPassword) is { } problem)
        {
            if (link.CreatedUserHost is not null) await DropCreatedUserAsync(link);
            return problem;
        }
        await DropCreatedUserAsync(await GetAsync(tenant));   // a reader PayLink made before is replaced
        await SaveAsync(tenant, link);
        await tenantDb.AuditAsync(tenant, actor, "crudapi.reader_set", link.ReadUser, link.CreatedUserHost is null ? "given" : "created by PayLink");
        return null;
    }

    /// <summary>Step 2 (optional): register the database in a CrudApi workspace and create a Reader client.</summary>
    public async Task<(bool Ok, string Message, string? ReaderSecret)> RegisterAsync(TenantRecord tenant, string? server, string? workspace, string? key, Actor actor)
    {
        if (await GetAsync(tenant) is not { ReadUser.Length: > 0 } link) return (false, "Set up the read-only user first.", null);
        if (string.IsNullOrWhiteSpace(server) || !Uri.TryCreate(server.Trim(), UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http"))
            return (false, "Enter CrudApi's address, e.g. https://crud.webapis.in.", null);
        if (string.IsNullOrWhiteSpace(key) || !key.StartsWith("wdk_", StringComparison.Ordinal))
            return (false, "Enter a workspace developer key (wdk_…) with the Databases and API credentials capabilities.", null);
        var baseUrl = uri.GetLeftPart(UriPartial.Authority) + uri.AbsolutePath.TrimEnd('/');
        if (await crudApi.CheckAsync(baseUrl, key.Trim()) is { } refused) return (false, refused, null);
        var (id, error) = await crudApi.RegisterAsync(baseUrl, key.Trim(), link, "paylink");
        if (error is not null) return (false, error + " You can register it by hand with the details below.", null);
        var (clientId, secret, readerError) = await crudApi.CreateReaderAsync(baseUrl, key.Trim(), "PayLink reader");
        link.Server = baseUrl;
        link.DeveloperKey = key.Trim();
        link.Workspace = string.IsNullOrWhiteSpace(workspace) ? null : workspace.Trim().ToLowerInvariant();
        link.RegistrationId = id;
        link.ReaderClientId = clientId;
        link.Note = readerError is null ? null : "Create a Reader API client in CrudApi (Keys) to read it: " + readerError;
        await SaveAsync(tenant, link);
        await tenantDb.AuditAsync(tenant, actor, "crudapi.registered", baseUrl, $"database {id}");
        return (true, readerError is null
            ? "Registered in CrudApi with a Reader client. Copy its secret now; it isn't shown again."
            : "Registered in CrudApi. " + link.Note, secret);
    }

    /// <summary>Removes the registration (when PayLink made it) and the read-only user (when PayLink made it).</summary>
    public async Task<string> DisconnectAsync(TenantRecord tenant, Actor actor)
    {
        if (await GetAsync(tenant) is not { } link) return "Not connected.";
        var notes = new List<string>();
        if (link is { Server: { } server, DeveloperKey: { } key, RegistrationId: { } id } && await crudApi.UnregisterAsync(server, key, id) is { } error)
            notes.Add($"CrudApi still lists the database ({error}); unregister it there.");
        if (link.CreatedUserHost is not null) await DropCreatedUserAsync(link);
        else notes.Add($"Remove the MySQL user {link.ReadUser} if nothing else uses it.");
        await SaveAsync(tenant, null);
        await tenantDb.AuditAsync(tenant, actor, "crudapi.disconnected", link.Server);
        return "Disconnected." + (notes.Count > 0 ? " " + string.Join(" ", notes) : "");
    }

    private async Task DropCreatedUserAsync(CrudApiLink? link)
    {
        if (link?.CreatedUserHost is null) return;
        if (DatabaseProvisioners.Create(await bootstrap.GetHostingAsync(), httpFactory.CreateClient("hosting")) is MySqlServerProvisioner p)
            await p.DropUserAsync(link.ReadUser, link.CreatedUserHost);
    }
}
