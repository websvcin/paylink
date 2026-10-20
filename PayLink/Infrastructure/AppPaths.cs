namespace PayLink.Infrastructure;

/// <summary>
/// Where PayLink keeps its files. Everything lives under one data folder (App_Data by default)
/// so a single volume mount or backup covers bootstrap.db, keys and SQLite databases.
/// </summary>
public sealed class AppPaths
{
    public AppPaths(IConfiguration configuration, IWebHostEnvironment environment)
    {
        var configured = configuration["PayLink:DataRoot"];
        DataRoot = Path.GetFullPath(string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(environment.ContentRootPath, "App_Data")
            : configured);
        DbScriptsRoot = Path.Combine(AppContext.BaseDirectory, "db");
        Directory.CreateDirectory(DataRoot);
        Directory.CreateDirectory(KeysDirectory);
        Directory.CreateDirectory(TenantsDirectory);
    }

    public string DataRoot { get; }
    public string DbScriptsRoot { get; }
    public string BootstrapFile => Path.Combine(DataRoot, "bootstrap.db");
    public string PlatformSqliteFile => Path.Combine(DataRoot, "platform.db");
    public string KeysDirectory => Path.Combine(DataRoot, "keys");
    public string TenantsDirectory => Path.Combine(DataRoot, "tenants");

    public string Scripts(string engineFolder, string kind) => Path.Combine(DbScriptsRoot, engineFolder, kind);
}
