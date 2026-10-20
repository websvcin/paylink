using System.Data.Common;
using Dapper;
using Microsoft.Data.Sqlite;
using MySqlConnector;
using Npgsql;

namespace WebSvc.PayLink.Storage;

/// <summary>Opens connections for any supported engine and hides the few SQL differences between them.</summary>
public static class Db
{
    public static async Task<DbConnection> OpenAsync(DatabaseTarget target, CancellationToken ct = default)
    {
        DbConnection connection = target.Engine switch
        {
            DbEngine.Sqlite => new SqliteConnection(target.ConnectionString),
            DbEngine.MySql => new MySqlConnection(target.ConnectionString),
            DbEngine.PostgreSql => new NpgsqlConnection(target.ConnectionString),
            _ => throw new NotSupportedException($"Engine {target.Engine} is not supported."),
        };
        await connection.OpenAsync(ct);
        if (target.Engine == DbEngine.Sqlite)
        {
            // WAL lets readers work while a write is in progress; busy_timeout waits instead of failing on a short lock.
            await connection.ExecuteAsync("PRAGMA journal_mode=WAL; PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000;");
        }
        return connection;
    }

    public static SqlDialect Dialect(DbEngine engine) => engine switch
    {
        DbEngine.Sqlite => SqlDialect.Sqlite,
        DbEngine.PostgreSql => SqlDialect.PostgreSql,
        _ => SqlDialect.MySql,
    };

    /// <summary>Counts the PayLink-visible tables (SQLite's own excluded) in the database the connection is on.</summary>
    public static string CountTablesSql(DbEngine engine) => engine switch
    {
        DbEngine.Sqlite => "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%';",
        DbEngine.PostgreSql => "SELECT COUNT(*) FROM information_schema.tables WHERE table_schema = current_schema();",
        _ => "SELECT COUNT(*) FROM information_schema.tables WHERE table_schema = DATABASE();",
    };
}

/// <summary>The handful of statements that differ between SQLite, MySQL and PostgreSQL.</summary>
public sealed class SqlDialect
{
    public static readonly SqlDialect Sqlite = new(DbEngine.Sqlite);
    public static readonly SqlDialect MySql = new(DbEngine.MySql);
    public static readonly SqlDialect PostgreSql = new(DbEngine.PostgreSql);

    private SqlDialect(DbEngine engine) => Engine = engine;

    public DbEngine Engine { get; }

    public string LastInsertId => Engine switch
    {
        DbEngine.Sqlite => "SELECT last_insert_rowid();",
        DbEngine.PostgreSql => "SELECT lastval();",
        _ => "SELECT LAST_INSERT_ID();",
    };

    /// <summary>Insert or replace one row identified by <paramref name="keyColumn"/>. Parameters are named after the columns.</summary>
    public string Upsert(string table, string keyColumn, params string[] valueColumns) => Upsert(table, [keyColumn], valueColumns);

    /// <summary>Insert or replace one row identified by several key columns (a composite primary key).</summary>
    public string Upsert(string table, IReadOnlyList<string> keyColumns, params string[] valueColumns)
    {
        var columns = keyColumns.Concat(valueColumns).ToArray();
        var insert = $"INSERT INTO {table} ({string.Join(", ", columns)}) VALUES ({string.Join(", ", columns.Select(c => "@" + c))})";
        return Engine is DbEngine.Sqlite or DbEngine.PostgreSql
            ? $"{insert} ON CONFLICT({string.Join(", ", keyColumns)}) DO UPDATE SET {string.Join(", ", valueColumns.Select(c => $"{c} = excluded.{c}"))};"
            : $"{insert} ON DUPLICATE KEY UPDATE {string.Join(", ", valueColumns.Select(c => $"{c} = VALUES({c})"))};";
    }
}
