using Dapper;
using MySqlConnector;

namespace WebSvc.PayLink.Storage.Provisioning;

/// <summary>Creates databases on a MySQL server PayLink can reach with an admin account.</summary>
public sealed class MySqlServerProvisioner(MySqlServerHosting hosting, string? connectionHostOverride = null) : IDatabaseProvisioner
{
    public HostingKind Kind => HostingKind.MySqlServer;

    private string AdminConnectionString => new MySqlConnectionStringBuilder
    {
        Server = hosting.Host,
        Port = hosting.Port,
        UserID = hosting.AdminUser,
        Password = hosting.AdminPassword,
        SslMode = hosting.SslMode,
        AllowPublicKeyRetrieval = hosting.SslMode == MySqlSslMode.None,
        ConnectionTimeout = 10,
    }.ToString();

    public async Task<ProvisioningCheck> TestAsync(CancellationToken ct = default)
    {
        ProvisionedDatabase? db = null;
        try
        {
            db = await CreateAsync("paylink_", "check", ct);
            var probe = await DatabaseProbe.TestAsync(db.Target, ct);
            if (!probe.Ok) return new ProvisioningCheck(false, "Created a test database but couldn't connect to it as its new user: " + probe.Message);
            return new ProvisioningCheck(true, $"Connected to MySQL {probe.ServerVersion}, created a test database and user, and removed them again.");
        }
        catch (Exception ex) when (ex is MySqlException or ProvisioningException)
        {
            return new ProvisioningCheck(false, ex.Message);
        }
        finally
        {
            if (db is not null) await DropAsync(db, CancellationToken.None);
        }
    }

    public async Task<ProvisionedDatabase> CreateAsync(string prefix, string label, CancellationToken ct = default)
    {
        var dbName = ProvisioningNames.DatabaseName(prefix, label);
        var user = ProvisioningNames.UserName();
        var password = ProvisioningNames.Password();
        ProvisioningNames.EnsureSafeIdentifier(dbName, "database name");
        ProvisioningNames.EnsureSafeIdentifier(user, "user name");
        var userHost = hosting.UserHost.Replace("'", "", StringComparison.Ordinal);

        try
        {
            await using var connection = new MySqlConnection(AdminConnectionString);
            await connection.OpenAsync(ct);
            // Identifiers can't be parameters; they are validated above to contain only [a-z0-9_].
            await connection.ExecuteAsync($"CREATE DATABASE `{dbName}` CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci;");
            await connection.ExecuteAsync($"CREATE USER '{user}'@'{userHost}' IDENTIFIED BY @password;", new { password });
            await connection.ExecuteAsync($"GRANT ALL PRIVILEGES ON `{dbName}`.* TO '{user}'@'{userHost}';");
        }
        catch (MySqlException ex)
        {
            await TryDrop(dbName, user, userHost);
            throw new ProvisioningException("MySQL refused to create the database: " + ex.Message, ex);
        }

        var target = DatabaseTarget.ForMySql(new MySqlConnectionDetails(
            connectionHostOverride ?? hosting.Host, hosting.Port, dbName, user, password, hosting.SslMode));
        return new ProvisionedDatabase(dbName, user, target, $"{dbName}|{user}|{userHost}");
    }

    public Task DropAsync(ProvisionedDatabase database, CancellationToken ct = default)
    {
        var parts = database.ProviderReference.Split('|');
        return TryDrop(parts[0], parts[1], parts.Length > 2 ? parts[2] : hosting.UserHost);
    }

    private async Task TryDrop(string dbName, string user, string userHost)
    {
        try
        {
            ProvisioningNames.EnsureSafeIdentifier(dbName, "database name");
            ProvisioningNames.EnsureSafeIdentifier(user, "user name");
            await using var connection = new MySqlConnection(AdminConnectionString);
            await connection.OpenAsync();
            await connection.ExecuteAsync($"DROP DATABASE IF EXISTS `{dbName}`;");
            await connection.ExecuteAsync($"DROP USER IF EXISTS '{user}'@'{userHost.Replace("'", "", StringComparison.Ordinal)}';");
        }
        catch (Exception ex) when (ex is MySqlException or ProvisioningException)
        {
            // Cleanup is best effort; the caller already reports the original failure.
        }
    }
}
