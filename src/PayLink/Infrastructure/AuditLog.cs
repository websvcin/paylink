using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using Dapper;
using WebSvc.PayLink.Storage;

namespace PayLink.Infrastructure;

/// <summary>Who did something: a person signed in through LoginLink, a developer key, or PayLink itself.</summary>
public sealed record Actor(string Type, string? Id, string? Name, string? Ip)
{
    public static Actor System => new("system", null, "PayLink", null);
    public static Actor Person(string subject, string? email, string? ip) => new("person", subject, email, ip);
    public static Actor Key(long keyId, string keyName, string? ip) => new("key", keyId.ToString(), keyName, ip);
}

public sealed class AuditEntry
{
    public long Id { get; set; }
    public string At { get; set; } = "";
    public string ActorType { get; set; } = "";
    public string? ActorId { get; set; }
    public string? ActorName { get; set; }
    public string? Ip { get; set; }
    public string Action { get; set; } = "";
    public string? Target { get; set; }
    public string? Details { get; set; }
    public string PrevHash { get; set; } = "";
    public string Hash { get; set; } = "";
}

/// <summary>
/// Append-only audit trail, used for the platform database and every tenant database (same table shape).
/// Each row's hash covers the previous row's hash and the row's content, so changing or deleting a row breaks the chain.
/// </summary>
public static class AuditLog
{
    private const string Genesis = "0000000000000000000000000000000000000000000000000000000000000000";
    private static readonly SemaphoreSlim WriteLock = new(1, 1);

    public static async Task WriteAsync(DbConnection connection, Actor actor, string action, string? target, string? details = null)
    {
        // Serialize writers in this process so two rows never claim the same previous hash.
        await WriteLock.WaitAsync();
        try
        {
            var prev = await connection.ExecuteScalarAsync<string?>("SELECT hash FROM audit_log ORDER BY id DESC LIMIT 1;") ?? Genesis;
            var entry = new AuditEntry
            {
                At = Clock.Now(), ActorType = actor.Type, ActorId = actor.Id, ActorName = actor.Name, Ip = actor.Ip,
                Action = action, Target = target, Details = details, PrevHash = prev,
            };
            entry.Hash = ComputeHash(entry);
            await connection.ExecuteAsync(
                "INSERT INTO audit_log (at, actor_type, actor_id, actor_name, ip, action, target, details, prev_hash, hash) " +
                "VALUES (@At, @ActorType, @ActorId, @ActorName, @Ip, @Action, @Target, @Details, @PrevHash, @Hash);", entry);
        }
        finally
        {
            WriteLock.Release();
        }
    }

    public static async Task<IReadOnlyList<AuditEntry>> RecentAsync(DbConnection connection, int take = 100, int skip = 0) =>
        (await connection.QueryAsync<AuditEntry>(
            "SELECT id, at, actor_type AS ActorType, actor_id AS ActorId, actor_name AS ActorName, ip, action, target, details, prev_hash AS PrevHash, hash " +
            "FROM audit_log ORDER BY id DESC LIMIT @take OFFSET @skip;", new { take, skip })).ToList();

    /// <summary>Walks the whole chain. Returns the id of the first broken row, or null when intact.</summary>
    public static async Task<long?> VerifyAsync(DbConnection connection)
    {
        var rows = await connection.QueryAsync<AuditEntry>(
            "SELECT id, at, actor_type AS ActorType, actor_id AS ActorId, actor_name AS ActorName, ip, action, target, details, prev_hash AS PrevHash, hash " +
            "FROM audit_log ORDER BY id;");
        var prev = Genesis;
        foreach (var row in rows)
        {
            if (row.PrevHash != prev || row.Hash != ComputeHash(row)) return row.Id;
            prev = row.Hash;
        }
        return null;
    }

    internal static string ComputeHash(AuditEntry e)
    {
        var material = string.Join("\u001f", e.PrevHash, e.At, e.ActorType, e.ActorId, e.ActorName, e.Ip, e.Action, e.Target, e.Details);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material))).ToLowerInvariant();
    }
}
