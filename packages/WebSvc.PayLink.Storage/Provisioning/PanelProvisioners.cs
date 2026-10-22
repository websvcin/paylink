using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MySqlConnector;

namespace WebSvc.PayLink.Storage.Provisioning;

/// <summary>
/// Creates databases through the Plesk REST API (v2): <c>POST /api/v2/databases</c> and <c>POST /api/v2/dbusers</c>.
/// Not yet verified against a live Plesk server; use "Test &amp; verify" before relying on it.
/// </summary>
public sealed class PleskProvisioner(PleskHosting hosting, HttpClient http, string? connectionHostOverride = null) : IDatabaseProvisioner
{
    public HostingKind Kind => HostingKind.Plesk;

    private Uri Api(string path) => new($"https://{hosting.Host}:{hosting.Port}/api/v2/{path}");

    private HttpRequestMessage Request(HttpMethod method, string path, object? body = null)
    {
        var request = new HttpRequestMessage(method, Api(path));
        if (!string.IsNullOrEmpty(hosting.SecretKey))
            request.Headers.Add("X-API-Key", hosting.SecretKey);
        else
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic",
                Convert.ToBase64String(Encoding.UTF8.GetBytes($"{hosting.Login}:{hosting.Password}")));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (body is not null) request.Content = JsonContent.Create(body);
        return request;
    }

    private async Task<JsonNode?> Send(HttpMethod method, string path, object? body, CancellationToken ct)
    {
        using var response = await http.SendAsync(Request(method, path, body), ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            throw new ProvisioningException($"Plesk refused the sign-in (401) for {path}. Check the admin login and password, or the secret key, and that the API is enabled for this account.");
        if (!response.IsSuccessStatusCode)
            throw new ProvisioningException($"Plesk answered {(int)response.StatusCode} for {path}: {Trim(text)}");
        return string.IsNullOrWhiteSpace(text) ? null : JsonNode.Parse(text);
    }

    public async Task<ProvisioningCheck> TestAsync(CancellationToken ct = default)
    {
        ProvisionedDatabase? db = null;
        try
        {
            db = await CreateAsync("paylink_", "check", ct);
            var left = await DropReportingAsync(db);
            db = null;
            return new ProvisioningCheck(true, left is null
                ? "Plesk created a test database and user, and removed them again."
                : $"Plesk created a test database and user, but didn't remove {left}. Delete it in Plesk; saving still works.");
        }
        catch (Exception ex) when (ex is ProvisioningException or HttpRequestException or TaskCanceledException or JsonException)
        {
            return new ProvisioningCheck(false, ex.Message);
        }
        finally
        {
            if (db is not null) await DropAsync(db, CancellationToken.None);
        }
    }

    /// <summary>Removes a database and its user; returns what's left behind (null when both are gone).</summary>
    private async Task<string?> DropReportingAsync(ProvisionedDatabase db)
    {
        var parts = db.ProviderReference.Split('|');
        var left = new List<string>();
        if (parts.Length > 1 && parts[1] != "0" && !await TryDelete($"dbusers/{parts[1]}")) left.Add($"the user {db.UserName}");
        if (!await TryDelete($"databases/{parts[0]}")) left.Add($"the database {db.DatabaseName}");
        return left.Count == 0 ? null : string.Join(" and ", left);
    }

    public string ProposeName(string prefix, string label) => ProvisioningNames.DatabaseName(prefix, label);

    public Task<ProvisionedDatabase> CreateAsync(string prefix, string label, CancellationToken ct = default) =>
        CreateNamedAsync(ProposeName(prefix, label), ct);

    public async Task<ProvisionedDatabase> CreateNamedAsync(string dbName, CancellationToken ct = default)
    {
        ProvisioningNames.EnsureSafeIdentifier(dbName, "database name");
        var servers = await Send(HttpMethod.Get, "dbservers", null, ct) as JsonArray
            ?? throw new ProvisioningException("Plesk returned no database servers.");
        var server = servers.FirstOrDefault(s => string.Equals((string?)s?["type"], "mysql", StringComparison.OrdinalIgnoreCase))
            ?? throw new ProvisioningException("Plesk has no MySQL database server.");
        var serverId = (int)server!["id"]!;
        var serverHost = (string?)server["host"] ?? hosting.Host;
        var serverPort = (uint?)(int?)server["port"] ?? 3306;
        // Plesk reports its local MySQL as "localhost"; from PayLink (often another machine or a container) that means the panel's server.
        if (serverHost is "localhost" or "127.0.0.1" or "::1") serverHost = hosting.Host;

        var created = await Send(HttpMethod.Post, "databases",
            new { name = dbName, type = "mysql", parent_domain = new { id = hosting.SubscriptionId }, server_id = serverId }, ct);
        var databaseId = (int?)created?["id"] ?? throw new ProvisioningException("Plesk didn't return the new database's id.");

        var user = ProvisioningNames.UserName();
        var password = ProvisioningNames.Password();
        try
        {
            var dbUser = await Send(HttpMethod.Post, "dbusers", new { login = user, password, database_id = databaseId }, ct);
            var userId = (int?)dbUser?["id"] ?? 0;
            var target = DatabaseTarget.ForMySql(new MySqlConnectionDetails(connectionHostOverride ?? serverHost, serverPort, dbName, user, password, MySqlSslMode.Preferred));
            return new ProvisionedDatabase(dbName, user, target, $"{databaseId}|{userId}");
        }
        catch
        {
            await TryDelete($"databases/{databaseId}");
            throw;
        }
    }

    public async Task DropAsync(ProvisionedDatabase database, CancellationToken ct = default)
    {
        var parts = database.ProviderReference.Split('|');
        if (parts.Length > 1 && parts[1] != "0") await TryDelete($"dbusers/{parts[1]}");
        await TryDelete($"databases/{parts[0]}");
    }

    private async Task<bool> TryDelete(string path)
    {
        try { await Send(HttpMethod.Delete, path, null, CancellationToken.None); return true; }
        catch (Exception ex) when (ex is ProvisioningException or HttpRequestException or TaskCanceledException) { return false; }
    }

    private static string Trim(string text) => text.Length > 300 ? text[..300] + "…" : text;
}

/// <summary>
/// Creates databases through cPanel UAPI (<c>Mysql::create_database</c>, <c>create_user</c>, <c>set_privileges_on_database</c>).
/// cPanel requires database and user names to start with the account name and an underscore.
/// Not yet verified against a live cPanel account; use "Test &amp; verify" before relying on it.
/// </summary>
public sealed class CPanelProvisioner(CPanelHosting hosting, HttpClient http, string? connectionHostOverride = null) : IDatabaseProvisioner
{
    public HostingKind Kind => HostingKind.CPanel;

    private async Task Call(string function, Dictionary<string, string> args, CancellationToken ct)
    {
        var query = string.Join("&", args.Select(kv => $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}"));
        using var request = new HttpRequestMessage(HttpMethod.Get, $"https://{hosting.Host}:{hosting.Port}/execute/Mysql/{function}?{query}");
        request.Headers.TryAddWithoutValidation("Authorization", $"cpanel {hosting.Username}:{hosting.ApiToken}");
        using var response = await http.SendAsync(request, ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            throw new ProvisioningException("cPanel refused the sign-in (401). Check the account user name and API token.");
        if (!response.IsSuccessStatusCode)
            throw new ProvisioningException($"cPanel answered {(int)response.StatusCode} for {function}.");
        var json = JsonNode.Parse(text);
        if ((int?)json?["status"] != 1)
        {
            var errors = json?["errors"] is JsonArray a ? string.Join("; ", a.Select(e => e?.ToString())) : "unknown error";
            throw new ProvisioningException($"cPanel refused {function}: {errors}");
        }
    }

    public async Task<ProvisioningCheck> TestAsync(CancellationToken ct = default)
    {
        ProvisionedDatabase? db = null;
        try
        {
            db = await CreateAsync("pl_", "check", ct);
            var parts = db.ProviderReference.Split('|');
            var left = new List<string>();
            if (!await TryCall("delete_user", new() { ["name"] = parts[1] })) left.Add($"the user {parts[1]}");
            if (!await TryCall("delete_database", new() { ["name"] = parts[0] })) left.Add($"the database {parts[0]}");
            db = null;
            return new ProvisioningCheck(true, left.Count == 0
                ? "cPanel created a test database and user, and removed them again."
                : $"cPanel created a test database and user, but didn't remove {string.Join(" and ", left)}. Delete it in cPanel; saving still works.");
        }
        catch (Exception ex) when (ex is ProvisioningException or HttpRequestException or TaskCanceledException or JsonException)
        {
            return new ProvisioningCheck(false, ex.Message);
        }
        finally
        {
            if (db is not null) await DropAsync(db, CancellationToken.None);
        }
    }

    private string Account => hosting.Username.ToLowerInvariant() + "_";

    public string ProposeName(string prefix, string label) => ProvisioningNames.DatabaseName(Account + prefix, label);

    public Task<ProvisionedDatabase> CreateAsync(string prefix, string label, CancellationToken ct = default) =>
        CreateNamedAsync(ProposeName(prefix, label), ct);

    public async Task<ProvisionedDatabase> CreateNamedAsync(string dbName, CancellationToken ct = default)
    {
        // cPanel only accepts names that start with the account name and an underscore.
        if (!dbName.StartsWith(Account, StringComparison.Ordinal)) dbName = Account + dbName;
        if (dbName.Length > 64) dbName = dbName[..64];
        ProvisioningNames.EnsureSafeIdentifier(dbName, "database name");
        var user = ProvisioningNames.UserName(Account);
        if (user.Length > 32) user = user[..32];
        var password = ProvisioningNames.Password();

        await Call("create_database", new() { ["name"] = dbName }, ct);
        try
        {
            await Call("create_user", new() { ["name"] = user, ["password"] = password }, ct);
            await Call("set_privileges_on_database", new() { ["user"] = user, ["database"] = dbName, ["privileges"] = "ALL PRIVILEGES" }, ct);
        }
        catch
        {
            await TryCall("delete_user", new() { ["name"] = user });
            await TryCall("delete_database", new() { ["name"] = dbName });
            throw;
        }
        var target = DatabaseTarget.ForMySql(new MySqlConnectionDetails(connectionHostOverride ?? hosting.Host, 3306, dbName, user, password, MySqlSslMode.Preferred));
        return new ProvisionedDatabase(dbName, user, target, $"{dbName}|{user}");
    }

    public async Task DropAsync(ProvisionedDatabase database, CancellationToken ct = default)
    {
        var parts = database.ProviderReference.Split('|');
        await TryCall("delete_user", new() { ["name"] = parts[1] });
        await TryCall("delete_database", new() { ["name"] = parts[0] });
    }

    private async Task<bool> TryCall(string function, Dictionary<string, string> args)
    {
        try { await Call(function, args, CancellationToken.None); return true; }
        catch (Exception ex) when (ex is ProvisioningException or HttpRequestException or TaskCanceledException or JsonException) { return false; }
    }
}
