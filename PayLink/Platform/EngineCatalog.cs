using WebSvc.PayLink.Storage;

namespace PayLink.Platform;

public sealed record EngineInfo(string Name, bool Available, string Note);

/// <summary>
/// Which database engines this build of PayLink can use. Availability comes from the engines the storage package
/// supports (its DbEngine values), so adding a provider there (e.g. PostgreSQL) lights it up everywhere without page changes.
/// </summary>
public static class EngineCatalog
{
    private static readonly (string Name, string EnumName, string Note)[] Known =
    [
        ("SQLite", nameof(DbEngine.Sqlite), "A file in the data folder. Nothing to set up."),
        ("MySQL", nameof(DbEngine.MySql), "Your own server, or created through auto-provisioning."),
        ("PostgreSQL", nameof(DbEngine.PostgreSql), "Your own server (PostgreSQL 13 or later). Auto-provisioning creates MySQL only."),
    ];

    public static IReadOnlyList<EngineInfo> All { get; } =
        Known.Select(k => new EngineInfo(k.Name, Enum.GetNames<DbEngine>().Contains(k.EnumName), k.Note)).ToList();
}

/// <summary>Which database choices tenants see at signup. Platform admins can always use any of them.</summary>
public static class TenantDbOptions
{
    public const string Sqlite = "sqlite";
    public const string Byo = "byo";
    public const string Auto = "auto";

    public static readonly IReadOnlyList<(string Key, string Label, string Help)> All =
    [
        (Sqlite, "SQLite, managed by PayLink", "A file in the data folder. Easiest to start with."),
        (Byo, "Their own MySQL or PostgreSQL", "The tenant enters a connection to an empty database they run."),
        (Auto, "Auto-provisioned MySQL", "Created on this platform's hosting, for organizations the auto-provisioning rules allow."),
    ];
}
