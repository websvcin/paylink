using Microsoft.Data.Sqlite;
using MySqlConnector;
using Npgsql;

namespace WebSvc.PayLink.Storage;

public enum DbEngine { Sqlite, MySql, PostgreSql }

/// <summary>Where a database lives: the engine plus a connection string. Never log <see cref="ConnectionString"/>; use <see cref="Describe"/>.</summary>
public sealed record DatabaseTarget(DbEngine Engine, string ConnectionString)
{
    public static DatabaseTarget ForSqliteFile(string path) =>
        new(DbEngine.Sqlite, new SqliteConnectionStringBuilder { DataSource = path, ForeignKeys = true, Pooling = true }.ToString());

    public static DatabaseTarget ForMySql(MySqlConnectionDetails details) => new(DbEngine.MySql, details.ToConnectionString());

    public static DatabaseTarget ForPostgreSql(PostgreSqlConnectionDetails details) => new(DbEngine.PostgreSql, details.ToConnectionString());

    /// <summary>A human-readable description with the password removed, safe for logs and the UI.</summary>
    public string Describe()
    {
        if (Engine == DbEngine.Sqlite)
        {
            var b = new SqliteConnectionStringBuilder(ConnectionString);
            return $"SQLite · {Path.GetFileName(b.DataSource)}";
        }
        if (Engine == DbEngine.PostgreSql)
        {
            var pg = new NpgsqlConnectionStringBuilder(ConnectionString);
            return $"PostgreSQL · {pg.Host}:{pg.Port}/{pg.Database}";
        }
        var m = new MySqlConnectionStringBuilder(ConnectionString);
        return $"MySQL · {m.Server}:{m.Port}/{m.Database}";
    }

    public string? SqliteFilePath => Engine == DbEngine.Sqlite ? new SqliteConnectionStringBuilder(ConnectionString).DataSource : null;
}

/// <summary>MySQL connection entered as separate fields.</summary>
public sealed record MySqlConnectionDetails(
    string Host,
    uint Port,
    string Database,
    string User,
    string Password,
    MySqlSslMode SslMode = MySqlSslMode.Preferred)
{
    public string ToConnectionString() => new MySqlConnectionStringBuilder
    {
        Server = Host,
        Port = Port,
        Database = Database,
        UserID = User,
        Password = Password,
        SslMode = SslMode,
        AllowUserVariables = true,
        // MySQL 8's default sign-in (caching_sha2_password) needs the server key when TLS is off.
        AllowPublicKeyRetrieval = SslMode == MySqlSslMode.None,
        ConnectionTimeout = 10,
    }.ToString();

    /// <summary>Parses a pasted MySQL connection string. Returns false with a plain reason when something required is missing.</summary>
    public static bool TryParse(string connectionString, out MySqlConnectionDetails? details, out string error)
    {
        details = null;
        try
        {
            var b = new MySqlConnectionStringBuilder(connectionString);
            if (string.IsNullOrWhiteSpace(b.Server)) { error = "The connection string has no Server."; return false; }
            if (string.IsNullOrWhiteSpace(b.Database)) { error = "The connection string has no Database."; return false; }
            if (string.IsNullOrWhiteSpace(b.UserID)) { error = "The connection string has no user (Uid or User ID)."; return false; }
            details = new MySqlConnectionDetails(b.Server, b.Port == 0 ? 3306 : b.Port, b.Database, b.UserID, b.Password ?? "", b.SslMode);
            error = "";
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException)
        {
            error = "That doesn't look like a MySQL connection string: " + ex.Message;
            return false;
        }
    }
}

/// <summary>PostgreSQL connection entered as separate fields.</summary>
public sealed record PostgreSqlConnectionDetails(
    string Host,
    int Port,
    string Database,
    string User,
    string Password,
    SslMode SslMode = SslMode.Prefer)
{
    public const int DefaultPort = 5432;

    public string ToConnectionString() => new NpgsqlConnectionStringBuilder
    {
        Host = Host,
        Port = Port,
        Database = Database,
        Username = User,
        Password = Password,
        SslMode = SslMode,
        Timeout = 10,
    }.ToString();

    /// <summary>Parses a pasted PostgreSQL connection string (key=value form). Returns false with a plain reason when something required is missing.</summary>
    public static bool TryParse(string connectionString, out PostgreSqlConnectionDetails? details, out string error)
    {
        details = null;
        try
        {
            var b = new NpgsqlConnectionStringBuilder(connectionString);
            if (string.IsNullOrWhiteSpace(b.Host)) { error = "The connection string has no Host."; return false; }
            if (string.IsNullOrWhiteSpace(b.Database)) { error = "The connection string has no Database."; return false; }
            if (string.IsNullOrWhiteSpace(b.Username)) { error = "The connection string has no user (Username)."; return false; }
            details = new PostgreSqlConnectionDetails(b.Host, b.Port == 0 ? DefaultPort : b.Port, b.Database, b.Username, b.Password ?? "", b.SslMode);
            error = "";
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException or KeyNotFoundException)
        {
            error = "That doesn't look like a PostgreSQL connection string (Host=…;Database=…;Username=…;Password=…): " + ex.Message;
            return false;
        }
    }
}
