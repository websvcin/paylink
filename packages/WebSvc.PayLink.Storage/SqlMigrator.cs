using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Dapper;

namespace WebSvc.PayLink.Storage;

/// <summary>One numbered SQL script, e.g. <c>0001_init.sql</c>.</summary>
public sealed record MigrationScript(string Version, string Name, string Sql)
{
    public string Checksum { get; } = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Sql.ReplaceLineEndings("\n")))).ToLowerInvariant();
}

public sealed record MigrationResult(IReadOnlyList<string> Applied, IReadOnlyList<string> AlreadyApplied);

public sealed class MigrationChecksumException(string message) : Exception(message);

/// <summary>
/// Applies plain SQL scripts in version order and records them in <c>schema_migrations</c>.
/// The same scripts can be run by hand by a database administrator; see db/README.md.
/// </summary>
public static partial class SqlMigrator
{
    [GeneratedRegex(@"^(?<version>\d{4})_(?<name>[a-z0-9_]+)\.sql$")]
    private static partial Regex ScriptName();

    /// <summary>Reads <c>NNNN_name.sql</c> files from a folder, ordered by version.</summary>
    public static IReadOnlyList<MigrationScript> LoadFromDirectory(string directory)
    {
        if (!Directory.Exists(directory))
            throw new DirectoryNotFoundException($"Migration folder not found: {directory}");

        return Directory.GetFiles(directory, "*.sql")
            .Select(path => (path, match: ScriptName().Match(Path.GetFileName(path))))
            .Where(x => x.match.Success)
            .Select(x => new MigrationScript(x.match.Groups["version"].Value, x.match.Groups["name"].Value, File.ReadAllText(x.path)))
            .OrderBy(s => s.Version, StringComparer.Ordinal)
            .ToList();
    }

    public static async Task<MigrationResult> ApplyAsync(DatabaseTarget target, IEnumerable<MigrationScript> scripts, CancellationToken ct = default)
    {
        await using var connection = await Db.OpenAsync(target, ct);
        await connection.ExecuteAsync(
            "CREATE TABLE IF NOT EXISTS schema_migrations (" +
            "version VARCHAR(20) NOT NULL PRIMARY KEY, " +
            "name VARCHAR(200) NOT NULL, " +
            "checksum VARCHAR(64) NOT NULL, " +
            "applied_at VARCHAR(40) NOT NULL);");

        var existing = (await connection.QueryAsync<(string Version, string Checksum)>("SELECT version, checksum FROM schema_migrations;"))
            .ToDictionary(r => r.Version, r => r.Checksum);

        var applied = new List<string>();
        var already = new List<string>();
        foreach (var script in scripts.OrderBy(s => s.Version, StringComparer.Ordinal))
        {
            if (existing.TryGetValue(script.Version, out var checksum))
            {
                if (!string.Equals(checksum, script.Checksum, StringComparison.OrdinalIgnoreCase))
                    throw new MigrationChecksumException(
                        $"Migration {script.Version}_{script.Name} was changed after it was applied. Add a new migration instead of editing an applied one.");
                already.Add(script.Version);
                continue;
            }

            if (target.Engine is DbEngine.Sqlite or DbEngine.PostgreSql)
            {
                // Both roll back DDL with the transaction, so a script applies completely or not at all.
                await using var tx = await connection.BeginTransactionAsync(ct);
                await connection.ExecuteAsync(script.Sql, transaction: tx);
                await Record(connection, script, tx);
                await tx.CommitAsync(ct);
            }
            else
            {
                // MySQL commits DDL implicitly, so a transaction would not protect a half-applied script.
                // Scripts use IF NOT EXISTS so that re-running after a failure is safe.
                await connection.ExecuteAsync(script.Sql);
                await Record(connection, script, null);
            }
            applied.Add(script.Version);
        }
        return new MigrationResult(applied, already);
    }

    /// <summary>Returns the highest applied version, or null for a database with no PayLink schema.</summary>
    public static async Task<string?> CurrentVersionAsync(DatabaseTarget target, CancellationToken ct = default)
    {
        await using var connection = await Db.OpenAsync(target, ct);
        var exists = await connection.ExecuteScalarAsync<long>(target.Engine switch
        {
            DbEngine.Sqlite => "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='schema_migrations';",
            DbEngine.PostgreSql => "SELECT COUNT(*) FROM information_schema.tables WHERE table_schema = current_schema() AND table_name = 'schema_migrations';",
            _ => "SELECT COUNT(*) FROM information_schema.tables WHERE table_schema = DATABASE() AND table_name = 'schema_migrations';",
        });
        if (exists == 0) return null;
        return await connection.ExecuteScalarAsync<string?>("SELECT MAX(version) FROM schema_migrations;");
    }

    private static Task Record(System.Data.Common.DbConnection connection, MigrationScript script, System.Data.Common.DbTransaction? tx) =>
        connection.ExecuteAsync(
            "INSERT INTO schema_migrations (version, name, checksum, applied_at) VALUES (@Version, @Name, @Checksum, @AppliedAt);",
            new { script.Version, script.Name, script.Checksum, AppliedAt = DateTime.UtcNow.ToString("O") },
            tx);
}
