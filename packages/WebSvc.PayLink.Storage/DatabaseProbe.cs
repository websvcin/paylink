using Dapper;
using Microsoft.Data.Sqlite;
using MySqlConnector;

namespace WebSvc.PayLink.Storage;

public sealed record ProbeResult(bool Ok, string Message, string? ServerVersion = null, long TableCount = 0, bool CanCreateTables = false)
{
    public bool IsEmpty => Ok && TableCount == 0;
}

/// <summary>Checks a database before PayLink uses it: can we connect, is it empty, may we create tables.</summary>
public static class DatabaseProbe
{
    public static async Task<ProbeResult> TestAsync(DatabaseTarget target, CancellationToken ct = default)
    {
        try
        {
            if (target.Engine == DbEngine.Sqlite && target.SqliteFilePath is { } path)
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);

            await using var connection = await Db.OpenAsync(target, ct);
            var version = connection.ServerVersion;
            var tables = await connection.ExecuteScalarAsync<long>(Db.CountTablesSql(target.Engine));

            var canCreate = false;
            try
            {
                await connection.ExecuteAsync("CREATE TABLE __paylink_probe (id INT); DROP TABLE __paylink_probe;");
                canCreate = true;
            }
            catch (Exception ex) when (ex is MySqlException or SqliteException or Npgsql.PostgresException)
            {
                return new ProbeResult(true, "Connected, but this user can't create tables: " + ex.Message, version, tables, false);
            }

            var message = tables == 0 ? "Connected. The database is empty." : $"Connected. The database already has {tables} table(s).";
            return new ProbeResult(true, message, version, tables, canCreate);
        }
        catch (Exception ex) when (ex is MySqlException or SqliteException or Npgsql.NpgsqlException or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            return new ProbeResult(false, Explain(ex));
        }
    }

    private static string Explain(Exception ex) => ex switch
    {
        MySqlException { ErrorCode: MySqlErrorCode.UnableToConnectToHost } =>
            $"Couldn't connect to the MySQL server from the PayLink server. Check the host, port, firewall and SSL mode. (MySQL: {ex.Message})",
        MySqlException { ErrorCode: MySqlErrorCode.AccessDenied } =>
            $"MySQL refused the user name or password. (MySQL: {ex.Message})",
        MySqlException { ErrorCode: MySqlErrorCode.UnknownDatabase } =>
            $"The database doesn't exist on that server. (MySQL: {ex.Message})",
        _ => ex.Message,
    };
}
