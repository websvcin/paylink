using Dapper;
using MySqlConnector;
using Npgsql;

namespace PayLink.Tests.Fakes;

/// <summary>
/// Runs the whole suite on a database server instead of SQLite: PAYLINK_TEST_PG_ADMIN (e.g. "Host=127.0.0.1;Username=postgres;Password=…")
/// for PostgreSQL, or PAYLINK_TEST_ALL_ON_MYSQL=1 with PAYLINK_TEST_MYSQL_ADMIN for MySQL. Setup puts the platform database there and
/// every tenant that would be SQLite gets its own new, empty database. The test host doesn't run exit handlers, so a run first drops
/// the databases earlier runs left (the paylink_t_ prefix is only used here); drop them by hand after the last run if the server is shared.
/// </summary>
public static class ServerDbTest
{
    private static string? PgAdmin => Environment.GetEnvironmentVariable("PAYLINK_TEST_PG_ADMIN");
    private static string? MySqlAdmin => Environment.GetEnvironmentVariable("PAYLINK_TEST_ALL_ON_MYSQL") is { Length: > 0 }
        ? Environment.GetEnvironmentVariable("PAYLINK_TEST_MYSQL_ADMIN") : null;

    public static bool OnPostgreSql => !string.IsNullOrEmpty(PgAdmin);
    public static bool On => OnPostgreSql || !string.IsNullOrEmpty(MySqlAdmin);
    /// <summary>The engine value the database forms take: "postgresql" or "mysql".</summary>
    public static string Engine => OnPostgreSql ? "postgresql" : "mysql";

    static ServerDbTest()
    {
        if (OnPostgreSql)
        {
            using var c = new NpgsqlConnection(PgAdmin);
            c.Open();
            foreach (var old in c.Query<string>("SELECT datname FROM pg_database WHERE datname LIKE 'paylink\\_t\\_%';"))
                c.Execute($"DROP DATABASE IF EXISTS {old} WITH (FORCE);");
        }
        else if (On)
        {
            using var c = new MySqlConnection(MySqlAdmin);
            c.Open();
            foreach (var old in c.Query<string>("SHOW DATABASES LIKE 'paylink\\_t\\_%';"))
                c.Execute($"DROP DATABASE IF EXISTS `{old}`;");
        }
    }

    /// <summary>Creates an empty database and returns its connection string.</summary>
    public static string NewDatabase()
    {
        var name = "paylink_t_" + Guid.NewGuid().ToString("N")[..12];
        if (OnPostgreSql)
        {
            using (var c = new NpgsqlConnection(PgAdmin)) { c.Open(); c.Execute($"CREATE DATABASE {name};"); }
            return new NpgsqlConnectionStringBuilder(PgAdmin) { Database = name }.ToString();
        }
        using (var c = new MySqlConnection(MySqlAdmin)) { c.Open(); c.Execute($"CREATE DATABASE `{name}` CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci;"); }
        return new MySqlConnectionStringBuilder(MySqlAdmin) { Database = name, SslMode = MySqlSslMode.None, AllowPublicKeyRetrieval = true }.ToString();
    }
}
