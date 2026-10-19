using System.Security.Cryptography;
using Dapper;
using PayLink.Infrastructure;
using PayLink.Tenancy;
using WebSvc.PayLink.Storage;
using Xunit;

namespace PayLink.Tests;

public sealed class UnitTests
{
    [Fact]
    public void Secrets_round_trip_and_detect_tampering()
    {
        var p = new SecretProtector(RandomNumberGenerator.GetBytes(32));
        var sealedValue = p.Protect("Pwd=s3cret");
        Assert.DoesNotContain("s3cret", sealedValue);
        Assert.Equal("Pwd=s3cret", p.Unprotect(sealedValue));

        var bytes = Convert.FromBase64String(sealedValue[3..]);
        bytes[^1] ^= 0xFF;
        Assert.ThrowsAny<CryptographicException>(() => p.Unprotect("v1:" + Convert.ToBase64String(bytes)));

        var other = new SecretProtector(RandomNumberGenerator.GetBytes(32));
        Assert.ThrowsAny<CryptographicException>(() => other.Unprotect(sealedValue));
    }

    [Fact]
    public async Task Audit_chain_detects_edits_and_deletions()
    {
        var file = Path.Combine(Path.GetTempPath(), $"audit-{Guid.NewGuid():N}.db");
        var target = DatabaseTarget.ForSqliteFile(file);
        try
        {
            await using var c = await Db.OpenAsync(target);
            await c.ExecuteAsync(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "db", "sqlite", "tenant", "0001_init.sql")));
            for (var i = 0; i < 5; i++) await AuditLog.WriteAsync(c, Actor.System, "test.event", $"t{i}");
            Assert.Null(await AuditLog.VerifyAsync(c));

            await c.ExecuteAsync("UPDATE audit_log SET target = 'changed' WHERE id = 3;");
            Assert.Equal(3, await AuditLog.VerifyAsync(c));

            await c.ExecuteAsync("UPDATE audit_log SET target = 't2' WHERE id = 3;");
            Assert.Null(await AuditLog.VerifyAsync(c));
            await c.ExecuteAsync("DELETE FROM audit_log WHERE id = 2;");
            Assert.Equal(3, await AuditLog.VerifyAsync(c));
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            File.Delete(file);
        }
    }

    [Theory]
    [InlineData("acme", null)]
    [InlineData("acme-learning-2", null)]
    [InlineData("a", "Use at least 2 characters.")]
    [InlineData("Acme", "Use lowercase letters")]
    [InlineData("-acme", "Use lowercase letters")]
    [InlineData("admin", "is reserved")]
    [InlineData("signup", "is reserved")]
    public void Slugs_are_validated(string slug, string? problem)
    {
        var result = Slugs.Problem(slug);
        if (problem is null) Assert.Null(result);
        else Assert.Contains(problem, result);
    }

    [Fact]
    public void Sqlite_and_mysql_scripts_define_the_same_tables()
    {
        foreach (var kind in new[] { "platform", "tenant" })
        {
            static IEnumerable<string> Tables(string dir) =>
                Directory.GetFiles(dir, "*.sql").SelectMany(f => System.Text.RegularExpressions.Regex
                    .Matches(File.ReadAllText(f), @"CREATE TABLE IF NOT EXISTS (\w+)").Select(m => m.Groups[1].Value)).OrderBy(t => t);
            var root = Path.Combine(AppContext.BaseDirectory, "db");
            Assert.Equal(Tables(Path.Combine(root, "sqlite", kind)), Tables(Path.Combine(root, "mysql", kind)));
            Assert.Equal(
                Directory.GetFiles(Path.Combine(root, "sqlite", kind)).Select(Path.GetFileName).Order(),
                Directory.GetFiles(Path.Combine(root, "mysql", kind)).Select(Path.GetFileName).Order());
        }
    }
}
