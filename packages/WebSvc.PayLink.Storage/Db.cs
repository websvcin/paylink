using System.Data.Common;
using Dapper;
using Microsoft.Data.Sqlite;
using MySqlConnector;

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

    public static SqlDialect Dialect(DbEngine engine) => engine == DbEngine.Sqlite ? SqlDialect.Sqlite : SqlDialect.MySql;
}

/// <summary>The handful of statements that differ between SQLite and MySQL.</summary>
public sealed class SqlDialect
{
    public static readonly SqlDialect Sqlite = new(DbEngine.Sqlite);
    public static readonly SqlDialect MySql = new(DbEngine.MySql);

    private SqlDialect(DbEngine engine) => Engine = engine;

    public DbEngine Engine { get; }

    public string LastInsertId => Engine == DbEngine.Sqlite ? "SELECT last_insert_rowid();" : "SELECT LAST_INSERT_ID();";

    /// <summary>Insert or replace one row identified by <paramref name="keyColumn"/>. Parameters are named after the columns.</summary>
    public string Upsert(string table, string keyColumn, params string[] valueColumns) => Upsert(table, [keyColumn], valueColumns);

    /// <summary>Insert or replace one row identified by several key columns (a composite primary key).</summary>
    public string Upsert(string table, IReadOnlyList<string> keyColumns, params string[] valueColumns)
    {
        var columns = keyColumns.Concat(valueColumns).ToArray();
        var insert = $"INSERT INTO {table} ({string.Join(", ", columns)}) VALUES ({string.Join(", ", columns.Select(c => "@" + c))})";
        return Engine == DbEngine.Sqlite
            ? $"{insert} ON CONFLICT({string.Join(", ", keyColumns)}) DO UPDATE SET {string.Join(", ", valueColumns.Select(c => $"{c} = excluded.{c}"))};"
            : $"{insert} ON DUPLICATE KEY UPDATE {string.Join(", ", valueColumns.Select(c => $"{c} = VALUES({c})"))};";
    }
}
