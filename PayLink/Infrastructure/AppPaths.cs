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
        // tenants/ is created with the first SQLite tenant, so the folder only holds what's in use.
    }

    public string DataRoot { get; }
    public bool IsDataRootConfigured { get; }
    public string AppFolder { get; }
    public string DbScriptsRoot { get; }
    public string BootstrapFile => Path.Combine(DataRoot, "bootstrap.db");
    public string PlatformSqliteFile => Path.Combine(DataRoot, "platform.db");
    /// <summary>Where earlier versions kept master.key and cookie keys; both are now inside bootstrap.db and imported from here once.</summary>
    public string KeysDirectory => Path.Combine(DataRoot, "keys");
    public string TenantsDirectory => Path.Combine(DataRoot, "tenants");
    public string SetupCodeFile => Path.Combine(DataRoot, "setup-code.txt");

    /// <summary>
    /// How a database target is stored: a SQLite file inside the data folder is kept as a path relative to it
    /// ("platform.db", "tenants/tenant_acme_k3f9.db"), so a backup restored or a folder moved to another server or
    /// drive still finds its files.
    /// </summary>
    public WebSvc.PayLink.Storage.DatabaseTarget Portable(WebSvc.PayLink.Storage.DatabaseTarget target)
    {
        if (target.SqliteFilePath is not { } file || !Path.IsPathRooted(file)) return target;
        var root = DataRoot.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(file);
        return full.StartsWith(root, StringComparison.OrdinalIgnoreCase)
            ? WebSvc.PayLink.Storage.DatabaseTarget.ForSqliteFile(Path.GetRelativePath(DataRoot, full).Replace('\\', '/'))
            : target;
    }

    /// <summary>
    /// The reverse of <see cref="Portable"/>. An absolute path saved by an earlier version that no longer exists
    /// (the folder was moved) is looked up at the same place inside this data folder.
    /// </summary>
    public WebSvc.PayLink.Storage.DatabaseTarget Resolve(WebSvc.PayLink.Storage.DatabaseTarget target)
    {
        if (target.SqliteFilePath is not { } file) return target;
        var windowsPath = file.Length > 2 && char.IsLetter(file[0]) && file[1] == ':';   // "D:\…" saved on Windows, read anywhere
        if (!Path.IsPathRooted(file) && !windowsPath)
            return WebSvc.PayLink.Storage.DatabaseTarget.ForSqliteFile(Path.Combine(DataRoot, file.Replace('/', Path.DirectorySeparatorChar)));
        if (!windowsPath || OperatingSystem.IsWindows()) { if (File.Exists(file)) return target; }
        var name = Path.GetFileName(file.Replace('\\', '/'));
        var parent = Path.GetFileName(Path.GetDirectoryName(file.Replace('\\', '/')) ?? "");
        var moved = parent == "tenants" ? Path.Combine(TenantsDirectory, name) : Path.Combine(DataRoot, name);
        return File.Exists(moved) ? WebSvc.PayLink.Storage.DatabaseTarget.ForSqliteFile(moved) : target;
    }

    public string Scripts(string engineFolder, string kind) => Path.Combine(DbScriptsRoot, engineFolder, kind);

    /// <summary>True when the data folder is inside the deployed app folder, where publishing a new version can overwrite it.</summary>
    public bool IsInsideAppFolder =>
        !RuntimeEnvironment.InContainer &&
        DataRoot.StartsWith(AppFolder.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
}
