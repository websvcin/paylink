using Dapper;
using Npgsql;

namespace PayLink.Tests.Fakes;

/// <summary>
/// Runs the whole suite on PostgreSQL when PAYLINK_TEST_PG_ADMIN is set (e.g. "Host=127.0.0.1;Username=postgres;Password=…"):
/// setup puts the platform database there and every tenant that would be SQLite gets its own PostgreSQL database.
/// Each database is new and empty. The test host doesn't run exit handlers, so a run first drops the databases earlier runs
/// left (the paylink_t_ prefix is only used here); drop them by hand after the last run if the server is shared.
/// </summary>
public static class PgTest
{
    public static string? Admin => Environment.GetEnvironmentVariable("PAYLINK_TEST_PG_ADMIN");
    public static bool On => !string.IsNullOrEmpty(Admin);

    static PgTest()
    {
        if (!On) return;
        using (var c = new NpgsqlConnection(Admin))
        {
            c.Open();
            foreach (var old in c.Query<string>("SELECT datname FROM pg_database WHERE datname LIKE 'paylink\\_t\\_%';"))
                c.Execute($"DROP DATABASE IF EXISTS {old} WITH (FORCE);");
        }
    }

    /// <summary>Creates an empty database and returns its connection string.</summary>
    public static string NewDatabase()
    {
        var name = "paylink_t_" + Guid.NewGuid().ToString("N")[..12];
        using (var c = new NpgsqlConnection(Admin)) { c.Open(); c.Execute($"CREATE DATABASE {name};"); }
        return new NpgsqlConnectionStringBuilder(Admin) { Database = name }.ToString();
    }
}
