namespace PayLink.Infrastructure;

/// <summary>One thing PayLink keeps in the data folder, and whether it's there yet.</summary>
public sealed record DataFolderItem(string Name, string Purpose, string CreatedWhen, bool Exists, long? Bytes, bool Secret);

/// <summary>
/// Lists what PayLink writes to the data folder, so nothing appears there unexplained.
/// Shown on the first setup step and in Admin → Settings.
/// </summary>
public static class DataFolder
{
    public static IReadOnlyList<DataFolderItem> Describe(AppPaths paths)
    {
        var root = paths.DataRoot;
        return
        [
            File("bootstrap.db", "Setup progress, where the platform database is (encrypted) and the auto-provisioning account (encrypted). Small, local SQLite, because it has to exist before any other database is known.",
                "When PayLink first starts."),
            File("bootstrap.db-wal, bootstrap.db-shm", "SQLite's write-ahead log and shared-memory index for bootstrap.db. Normal while PayLink runs; SQLite folds them back in. Back them up together with the .db file, or stop PayLink first.",
                "With bootstrap.db.", "bootstrap.db-wal"),
            File("keys/master.key", "The key that encrypts every stored secret (connection strings, passwords, client secrets). Without it they can't be read. Not used when PayLink__MasterKey is set.",
                "When PayLink first starts.", secret: true),
            Folder("keys/dataprotection/", "ASP.NET Core keys that sign sign-in cookies and forms. Losing them only signs everyone out.",
                "When PayLink first starts.", secret: true),
            File("setup-code.txt", "The one-time setup code. Deleted when setup finishes.", "When PayLink starts and setup isn't finished."),
            File("platform.db", "The platform database, only when you choose SQLite for it: tenants, admins, developer keys and the platform audit log.",
                "In setup step 3, if you choose SQLite."),
            Folder("tenants/", "One SQLite file per tenant that chose SQLite: its team, settings, payments and audit log.",
                "When the first tenant on SQLite is created."),
        ];

        DataFolderItem File(string name, string purpose, string when, string? probe = null, bool secret = false)
        {
            var path = Path.Combine(root, (probe ?? name).Replace('/', Path.DirectorySeparatorChar));
            var info = new FileInfo(path);
            return new(name, purpose, when, info.Exists, info.Exists ? info.Length : null, secret);
        }

        DataFolderItem Folder(string name, string purpose, string when, bool secret = false)
        {
            var dir = new DirectoryInfo(Path.Combine(root, name.TrimEnd('/').Replace('/', Path.DirectorySeparatorChar)));
            long? bytes = dir.Exists ? dir.EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length) : null;
            return new(name, purpose, when, dir.Exists, bytes, secret);
        }
    }

    public static string Size(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        _ => $"{bytes / 1024.0 / 1024.0:0.#} MB",
    };
}
