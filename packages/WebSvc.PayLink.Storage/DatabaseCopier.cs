using System.Data.Common;
using Dapper;

namespace WebSvc.PayLink.Storage;

public sealed record CopyResult(IReadOnlyDictionary<string, long> Rows);

/// <summary>
/// Copies every table's rows from one database to another of the same schema, on any pair of engines (SQLite ↔ MySQL).
/// The target's schema must already exist (run the migrations first); rows the migrations seeded there are replaced.
/// schema_migrations is left alone on both sides. Row counts are checked after copying.
/// </summary>
public static class DatabaseCopier
{
    public static async Task<IReadOnlyList<string>> TablesAsync(DbConnection c, DbEngine engine) =>
        (await c.QueryAsync<string>(engine == DbEngine.Sqlite
            ? "SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%' ORDER BY name;"
            : "SELECT table_name FROM information_schema.tables WHERE table_schema = DATABASE() AND table_type = 'BASE TABLE' ORDER BY table_name;"))
        .Where(t => !string.Equals(t, "schema_migrations", StringComparison.OrdinalIgnoreCase)).ToList();

    public static async Task<CopyResult> CopyAsync(DatabaseTarget source, DatabaseTarget target, int batchSize = 500, CancellationToken ct = default)
    {
        await using var from = await Db.OpenAsync(source, ct);
        await using var to = await Db.OpenAsync(target, ct);
        var sourceTables = await TablesAsync(from, source.Engine);
        var targetTables = (await TablesAsync(to, target.Engine)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var missing = sourceTables.Where(t => !targetTables.Contains(t)).ToList();
        if (missing.Count > 0) throw new InvalidOperationException($"The target database has no table {string.Join(", ", missing)}; run its migrations first.");

        var counts = new Dictionary<string, long>();
        foreach (var table in sourceTables)
        {
            ct.ThrowIfCancellationRequested();
            await to.ExecuteAsync($"DELETE FROM {table};");
            long copied = 0;
            var offset = 0;
            while (true)
            {
                var rows = (await from.QueryAsync($"SELECT * FROM {table} LIMIT {batchSize} OFFSET {offset};")).Cast<IDictionary<string, object?>>().ToList();
                if (rows.Count == 0) break;
                var columns = rows[0].Keys.ToList();
                var sql = $"INSERT INTO {table} ({string.Join(", ", columns)}) VALUES ({string.Join(", ", columns.Select((_, i) => "@p" + i))});";
                await using var tx = await to.BeginTransactionAsync(ct);
                foreach (var row in rows)
                {
                    var p = new DynamicParameters();
                    for (var i = 0; i < columns.Count; i++) p.Add("p" + i, row[columns[i]]);
                    await to.ExecuteAsync(sql, p, tx);
                }
                await tx.CommitAsync(ct);
                copied += rows.Count;
                offset += rows.Count;
            }
            var targetCount = await to.ExecuteScalarAsync<long>($"SELECT COUNT(*) FROM {table};");
            if (targetCount != copied) throw new InvalidOperationException($"{table}: copied {copied} rows but the target has {targetCount}.");
            counts[table] = copied;
        }
        return new CopyResult(counts);
    }
}
