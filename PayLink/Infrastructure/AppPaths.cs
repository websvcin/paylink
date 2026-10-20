namespace PayLink.Infrastructure;

/// <summary>
/// Where PayLink keeps its files. Everything lives under one data folder (App_Data by default)
/// so a single volume mount or backup covers bootstrap.db, keys and SQLite databases.
/// Set PayLink__DataRoot (or "PayLink": { "DataRoot": … } in appsettings.json) to move it.
/// </summary>
public sealed class AppPaths
{
    public const string DataRootVariable = "PayLink__DataRoot";

    public AppPaths(IConfiguration configuration, IWebHostEnvironment environment)
    {
        var configured = configuration["PayLink:DataRoot"];
        IsDataRootConfigured = !string.IsNullOrWhiteSpace(configured);
        DataRoot = Path.GetFullPath(IsDataRootConfigured
            ? configured!
            : Path.Combine(environment.ContentRootPath, "App_Data"));
        AppFolder = Path.GetFullPath(environment.ContentRootPath);
        DbScriptsRoot = Path.Combine(AppContext.BaseDirectory, "db");
        Directory.CreateDirectory(DataRoot);
        Directory.CreateDirectory(KeysDirectory);
        Directory.CreateDirectory(TenantsDirectory);
    }

    public string DataRoot { get; }
    public bool IsDataRootConfigured { get; }
    public string AppFolder { get; }
    public string DbScriptsRoot { get; }
    public string BootstrapFile => Path.Combine(DataRoot, "bootstrap.db");
    public string PlatformSqliteFile => Path.Combine(DataRoot, "platform.db");
    public string KeysDirectory => Path.Combine(DataRoot, "keys");
    public string TenantsDirectory => Path.Combine(DataRoot, "tenants");
    public string SetupCodeFile => Path.Combine(DataRoot, "setup-code.txt");

    public string Scripts(string engineFolder, string kind) => Path.Combine(DbScriptsRoot, engineFolder, kind);

    /// <summary>True when the data folder is inside the deployed app folder, where publishing a new version can overwrite it.</summary>
    public bool IsInsideAppFolder =>
        !RuntimeEnvironment.InContainer &&
        DataRoot.StartsWith(AppFolder.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
}
