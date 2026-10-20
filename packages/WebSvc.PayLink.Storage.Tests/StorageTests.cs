using Dapper;
using WebSvc.PayLink.Storage;
using WebSvc.PayLink.Storage.Provisioning;
using Xunit;

namespace WebSvc.PayLink.Storage.Tests;

public sealed class StorageTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "paylink-storage-" + Guid.NewGuid().ToString("N"));

    public StorageTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(_dir, recursive: true);
    }

    private static MigrationScript Script(string version, string sql) => new(version, "test", sql);

    [Fact]
    public async Task Migrations_apply_once_in_order()
    {
        var target = DatabaseTarget.ForSqliteFile(Path.Combine(_dir, "m.db"));
        var scripts = new[]
        {
            Script("0002", "ALTER TABLE things ADD COLUMN label TEXT;"),
            Script("0001", "CREATE TABLE things (id INTEGER PRIMARY KEY);"),
        };

        var first = await SqlMigrator.ApplyAsync(target, scripts);
        var second = await SqlMigrator.ApplyAsync(target, scripts);

        Assert.Equal(new[] { "0001", "0002" }, first.Applied);
        Assert.Empty(second.Applied);
        Assert.Equal("0002", await SqlMigrator.CurrentVersionAsync(target));
    }

    [Fact]
    public async Task Editing_an_applied_migration_is_refused()
    {
        var target = DatabaseTarget.ForSqliteFile(Path.Combine(_dir, "c.db"));
        await SqlMigrator.ApplyAsync(target, [Script("0001", "CREATE TABLE a (id INTEGER);")]);

        await Assert.ThrowsAsync<MigrationChecksumException>(() =>
            SqlMigrator.ApplyAsync(target, [Script("0001", "CREATE TABLE a (id INTEGER, x TEXT);")]));
    }

    [Fact]
    public async Task Failed_sqlite_migration_leaves_nothing_behind()
    {
        var target = DatabaseTarget.ForSqliteFile(Path.Combine(_dir, "f.db"));
        await Assert.ThrowsAnyAsync<Exception>(() =>
            SqlMigrator.ApplyAsync(target, [Script("0001", "CREATE TABLE ok (id INTEGER); THIS IS NOT SQL;")]));

        await using var c = await Db.OpenAsync(target);
        Assert.Equal(0, await c.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM sqlite_master WHERE name='ok';"));
        Assert.Null(await SqlMigrator.CurrentVersionAsync(target));
    }

    [Fact]
    public async Task Probe_reports_empty_and_non_empty()
    {
        var target = DatabaseTarget.ForSqliteFile(Path.Combine(_dir, "sub", "p.db"));
        var empty = await DatabaseProbe.TestAsync(target);
        Assert.True(empty.IsEmpty);
        Assert.True(empty.CanCreateTables);

        await using (var c = await Db.OpenAsync(target)) await c.ExecuteAsync("CREATE TABLE x (id INTEGER);");
        var full = await DatabaseProbe.TestAsync(target);
        Assert.False(full.IsEmpty);
        Assert.Equal(1, full.TableCount);
    }

    [Fact]
    public async Task Upsert_works_on_sqlite()
    {
        var target = DatabaseTarget.ForSqliteFile(Path.Combine(_dir, "u.db"));
        await using var c = await Db.OpenAsync(target);
        await c.ExecuteAsync("CREATE TABLE kv (k VARCHAR(50) PRIMARY KEY, v TEXT);");
        var sql = SqlDialect.Sqlite.Upsert("kv", "k", "v");
        await c.ExecuteAsync(sql, new { k = "a", v = "1" });
        await c.ExecuteAsync(sql, new { k = "a", v = "2" });
        Assert.Equal("2", await c.ExecuteScalarAsync<string>("SELECT v FROM kv WHERE k='a';"));
    }

    [Theory]
    [InlineData("Server=db.acme.in;Port=3307;Database=pay;Uid=u;Pwd=p", true, "db.acme.in", 3307u)]
    [InlineData("Server=db.acme.in;Database=pay;Uid=u", true, "db.acme.in", 3306u)]
    [InlineData("Server=db.acme.in;Uid=u", false, "", 0u)]
    [InlineData("not a connection string at all ===", false, "", 0u)]
    public void Parses_pasted_mysql_connection_strings(string cs, bool ok, string host, uint port)
    {
        Assert.Equal(ok, MySqlConnectionDetails.TryParse(cs, out var d, out var error));
        if (ok) { Assert.Equal(host, d!.Host); Assert.Equal(port, d.Port); }
        else Assert.False(string.IsNullOrEmpty(error));
    }

    [Fact]
    public void Describe_never_shows_the_password()
    {
        var t = DatabaseTarget.ForMySql(new MySqlConnectionDetails("h", 3306, "d", "u", "s3cret!"));
        Assert.DoesNotContain("s3cret", t.Describe());
    }

    [Fact]
    public void Generated_names_are_safe_identifiers()
    {
        var name = ProvisioningNames.DatabaseName("paylink_", "Acme Corp; DROP TABLE x--");
        ProvisioningNames.EnsureSafeIdentifier(name, "database name");
        Assert.StartsWith("paylink_acme_corp", name);
        Assert.True(name.Length <= 64);
    }

    /// <summary>Runs only when PAYLINK_TEST_MYSQL_ADMIN is set, e.g. "Server=127.0.0.1;Uid=root;Pwd=…".</summary>
    [Fact]
    public async Task MySql_provisioner_creates_migrates_and_drops()
    {
        var admin = Environment.GetEnvironmentVariable("PAYLINK_TEST_MYSQL_ADMIN");
        if (string.IsNullOrEmpty(admin)) return;
        var b = new MySqlConnector.MySqlConnectionStringBuilder(admin);
        var provisioner = new MySqlServerProvisioner(new MySqlServerHosting
        {
            Host = b.Server, Port = b.Port == 0 ? 3306 : b.Port, AdminUser = b.UserID, AdminPassword = b.Password,
            SslMode = MySqlConnector.MySqlSslMode.None, UserHost = "%",
        });

        var check = await provisioner.TestAsync();
        Assert.True(check.Ok, check.Message);

        var db = await provisioner.CreateAsync("paylink_", "storagetest");
        try
        {
            var result = await SqlMigrator.ApplyAsync(db.Target, [Script("0001", "CREATE TABLE IF NOT EXISTS t (id BIGINT AUTO_INCREMENT PRIMARY KEY, k VARCHAR(20) UNIQUE, v TEXT);")]);
            Assert.Single(result.Applied);
            await using var c = await Db.OpenAsync(db.Target);
            var sql = SqlDialect.MySql.Upsert("t", "k", "v");
            await c.ExecuteAsync(sql, new { k = "a", v = "1" });
            await c.ExecuteAsync(sql, new { k = "a", v = "2" });
            Assert.Equal("2", await c.ExecuteScalarAsync<string>("SELECT v FROM t WHERE k='a';"));
        }
        finally
        {
            await provisioner.DropAsync(db);
        }
        // Pooled sessions outlive DROP USER; clear them so the probe opens a fresh connection.
        await MySqlConnector.MySqlConnection.ClearAllPoolsAsync();
        var gone = await DatabaseProbe.TestAsync(db.Target);
        Assert.False(gone.Ok, gone.Message);
    }

    [Fact]
    public async Task Copier_moves_every_row_and_replaces_seeded_ones()
    {
        var schema = new[] { Script("0001", "CREATE TABLE a (id INTEGER PRIMARY KEY, name TEXT, amount BIGINT); CREATE TABLE b (k VARCHAR(10) PRIMARY KEY, v TEXT); INSERT INTO b (k, v) VALUES ('seed', 'x');") };
        var source = DatabaseTarget.ForSqliteFile(Path.Combine(_dir, "src.db"));
        var target = DatabaseTarget.ForSqliteFile(Path.Combine(_dir, "dst.db"));
        await SqlMigrator.ApplyAsync(source, schema);
        await SqlMigrator.ApplyAsync(target, schema);
        await using (var c = await Db.OpenAsync(source))
        {
            for (var i = 1; i <= 1203; i++) await c.ExecuteAsync("INSERT INTO a (id, name, amount) VALUES (@i, @n, @m);", new { i, n = "row" + i, m = i * 100L });
            await c.ExecuteAsync("UPDATE b SET v = 'from source';");
        }
        var result = await DatabaseCopier.CopyAsync(source, target, batchSize: 250);
        Assert.Equal(1203, result.Rows["a"]);
        Assert.Equal(1, result.Rows["b"]);
        await using var d = await Db.OpenAsync(target);
        Assert.Equal(120300L, await d.ExecuteScalarAsync<long>("SELECT amount FROM a WHERE id = 1203;"));
        Assert.Equal("from source", await d.ExecuteScalarAsync<string>("SELECT v FROM b WHERE k = 'seed';"));
        Assert.Equal("0001", await SqlMigrator.CurrentVersionAsync(target));
    }

    /// <summary>Runs only when PAYLINK_TEST_PG_ADMIN is set, e.g. "Host=127.0.0.1;Username=postgres;Password=…".</summary>
    [Fact]
    public async Task PostgreSql_migrates_upserts_and_takes_a_copy_from_sqlite()
    {
        var admin = Environment.GetEnvironmentVariable("PAYLINK_TEST_PG_ADMIN");
        if (string.IsNullOrEmpty(admin)) return;
        var name = "paylink_storagetest_" + Guid.NewGuid().ToString("N")[..8];
        await using (var a = new Npgsql.NpgsqlConnection(admin)) { await a.OpenAsync(); await a.ExecuteAsync($"CREATE DATABASE {name};"); }
        var b = new Npgsql.NpgsqlConnectionStringBuilder(admin);
        var target = DatabaseTarget.ForPostgreSql(new PostgreSqlConnectionDetails(b.Host!, b.Port, name, b.Username!, b.Password ?? "", Npgsql.SslMode.Disable));
        try
        {
            Assert.Equal($"PostgreSQL · {b.Host}:{b.Port}/{name}", target.Describe());
            var probe = await DatabaseProbe.TestAsync(target);
            Assert.True(probe.Ok && probe.CanCreateTables && probe.TableCount == 0, probe.Message);

            var pg = new[] { Script("0001", "CREATE TABLE a (id BIGINT GENERATED BY DEFAULT AS IDENTITY PRIMARY KEY, name TEXT, live BOOLEAN NOT NULL DEFAULT FALSE); CREATE TABLE b (k VARCHAR(10) PRIMARY KEY, v TEXT);") };
            Assert.Single((await SqlMigrator.ApplyAsync(target, pg)).Applied);
            Assert.Equal("0001", await SqlMigrator.CurrentVersionAsync(target));
            // A failing script leaves nothing behind: PostgreSQL rolls DDL back.
            await Assert.ThrowsAnyAsync<Exception>(() => SqlMigrator.ApplyAsync(target, [.. pg, Script("0002", "CREATE TABLE half (id INT); SELECT nope();")]));
            await using (var c = await Db.OpenAsync(target))
            {
                Assert.Equal(0L, await c.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM information_schema.tables WHERE table_name = 'half';"));
                var upsert = SqlDialect.PostgreSql.Upsert("b", "k", "v");
                await c.ExecuteAsync(upsert, new { k = "a", v = "1" });
                await c.ExecuteAsync(upsert, new { k = "a", v = "2" });
                Assert.Equal("2", await c.ExecuteScalarAsync<string>("SELECT v FROM b WHERE k='a';"));
            }

            // SQLite (0/1 flags, explicit ids) into PostgreSQL (booleans, identity).
            var source = DatabaseTarget.ForSqliteFile(Path.Combine(_dir, "src.db"));
            await SqlMigrator.ApplyAsync(source, [Script("0001", "CREATE TABLE a (id INTEGER PRIMARY KEY AUTOINCREMENT, name TEXT, live INTEGER NOT NULL DEFAULT 0); CREATE TABLE b (k VARCHAR(10) PRIMARY KEY, v TEXT);")]);
            await using (var c = await Db.OpenAsync(source))
                for (var i = 1; i <= 30; i++) await c.ExecuteAsync("INSERT INTO a (name, live) VALUES (@n, @l);", new { n = "row" + i, l = i % 2 });
            var copied = await DatabaseCopier.CopyAsync(source, target);
            Assert.Equal(30, copied.Rows["a"]);
            await using (var c = await Db.OpenAsync(target))
            {
                Assert.Equal(15L, await c.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM a WHERE live = TRUE;"));
                await c.ExecuteAsync("INSERT INTO a (name, live) VALUES ('next', TRUE);");
                Assert.Equal(31L, await c.ExecuteScalarAsync<long>(SqlDialect.PostgreSql.LastInsertId));
            }
        }
        finally
        {
            Npgsql.NpgsqlConnection.ClearAllPools();
            await using var a = new Npgsql.NpgsqlConnection(admin);
            await a.OpenAsync();
            await a.ExecuteAsync($"DROP DATABASE IF EXISTS {name} WITH (FORCE);");
        }
    }
}
