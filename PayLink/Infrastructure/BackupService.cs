using System.IO.Compression;
using System.Text.Json;
using Dapper;
using Microsoft.Data.Sqlite;
using PayLink.Platform;
using PayLink.Tenancy;
using WebSvc.PayLink.Storage;

namespace PayLink.Infrastructure;

public sealed record RestoreResult(bool Ok, string Message, bool SetupComplete = false);

/// <summary>
/// Backs up and restores the data folder. A backup is a .zip holding consistent copies of bootstrap.db, platform.db
/// (when the platform database is SQLite), every SQLite tenant database and the uploaded files. Databases on MySQL are
/// not inside it: back those up with your MySQL tooling. A restore accepts such a .zip, or a bootstrap.db on its own.
/// </summary>
public sealed class BackupService(AppPaths paths, SecretProtector secrets, PlatformDb platformDb, TenantDb tenantDb, ILogger<BackupService> logger)
{
    public const long MaxUploadBytes = 2L * 1024 * 1024 * 1024;

    public async Task WriteBackupAsync(Stream output, CancellationToken ct = default)
    {
        var work = Path.Combine(Path.GetTempPath(), "paylink-backup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            using var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);
            await AddDatabaseAsync(zip, work, paths.BootstrapFile, "bootstrap.db");
            await AddDatabaseAsync(zip, work, paths.PlatformSqliteFile, "platform.db");
            if (Directory.Exists(paths.TenantsDirectory))
                foreach (var file in Directory.GetFiles(paths.TenantsDirectory, "*.db"))
                    await AddDatabaseAsync(zip, work, file, "tenants/" + Path.GetFileName(file));
            var files = Path.Combine(paths.DataRoot, "files");
            if (Directory.Exists(files))
                foreach (var file in Directory.GetFiles(files, "*", SearchOption.AllDirectories))
                    zip.CreateEntryFromFile(file, "files/" + Path.GetRelativePath(files, file).Replace('\\', '/'));

            var platform = await SafePlatformDescriptionAsync();
            var manifest = zip.CreateEntry("paylink-backup.json");
            await using var m = manifest.Open();
            await JsonSerializer.SerializeAsync(m, new
            {
                product = "PayLink",
                version = typeof(Program).Assembly.GetName().Version?.ToString(3),
                created_at = Clock.Now(),
                platform_database = platform,
                encryption_key = secrets.Source == "environment"
                    ? "Not inside this backup: it's in PayLink__MasterKey. Keep that value with the backup."
                    : "Inside bootstrap.db.",
                note = "Databases on MySQL aren't in this file; back them up with your MySQL tools.",
            }, new JsonSerializerOptions { WriteIndented = true }, ct);
        }
        finally
        {
            try { Directory.Delete(work, true); } catch (IOException) { }
        }
    }

    private async Task<string> SafePlatformDescriptionAsync()
    {
        try { return (await platformDb.TargetAsync()).Describe(); }
        catch (Exception) { return "not set up yet"; }
    }

    /// <summary>Copies a live SQLite database consistently (VACUUM INTO) and adds the copy to the archive.</summary>
    private static async Task AddDatabaseAsync(ZipArchive zip, string work, string source, string entryName)
    {
        if (!File.Exists(source)) return;
        var copy = Path.Combine(work, Guid.NewGuid().ToString("N") + ".db");
        await using (var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = source, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString()))
        {
            await c.OpenAsync();
            await c.ExecuteAsync("VACUUM INTO @copy;", new { copy });
        }
        zip.CreateEntryFromFile(copy, entryName, CompressionLevel.Optimal);
    }

    /// <summary>
    /// Restores a backup (.zip) or a bootstrap.db into the data folder. Everything is checked before anything is replaced;
    /// the files being replaced are kept in a before-restore-… folder.
    /// </summary>
    public async Task<RestoreResult> RestoreAsync(Stream upload, string fileName, CancellationToken ct = default)
    {
        var work = Path.Combine(Path.GetTempPath(), "paylink-restore-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            var received = Path.Combine(work, "upload");
            await using (var f = File.Create(received)) await upload.CopyToAsync(f, ct);
            var staged = Path.Combine(work, "staged");
            Directory.CreateDirectory(staged);

            var header = new byte[16];
            await using (var f = File.OpenRead(received)) _ = await f.ReadAtLeastAsync(header, 16, throwOnEndOfStream: false);
            if (header[0] == 'P' && header[1] == 'K')
            {
                if (ExtractBackup(received, staged) is { } problem) return new(false, problem);
            }
            else if (IsSqlite(header)) File.Copy(received, Path.Combine(staged, "bootstrap.db"));
            else return new(false, $"{fileName} isn't a PayLink backup (.zip) or a bootstrap.db.");

            var bootstrapFile = Path.Combine(staged, "bootstrap.db");
            if (!File.Exists(bootstrapFile)) return new(false, "The backup has no bootstrap.db.");
            if (CheckBootstrap(bootstrapFile, staged) is { } invalid) return new(false, invalid);

            // Swap in the new files, keeping the current ones aside.
            SqliteConnection.ClearAllPools();
            var aside = Path.Combine(paths.DataRoot, "before-restore-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss"));
            Directory.CreateDirectory(aside);
            foreach (var name in new[] { "bootstrap.db", "bootstrap.db-wal", "bootstrap.db-shm", "platform.db", "platform.db-wal", "platform.db-shm" })
            {
                var current = Path.Combine(paths.DataRoot, name);
                if (File.Exists(current)) File.Move(current, Path.Combine(aside, name));
            }
            foreach (var entry in Directory.GetFileSystemEntries(staged, "*", SearchOption.AllDirectories).Where(File.Exists))
            {
                var relative = Path.GetRelativePath(staged, entry);
                var target = Path.Combine(paths.DataRoot, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                if (File.Exists(target)) { var keep = Path.Combine(aside, relative); Directory.CreateDirectory(Path.GetDirectoryName(keep)!); File.Move(target, keep); }
                File.Move(entry, target);
            }

            secrets.Reload();
            platformDb.Reset();
            tenantDb.ClearCache();
            var complete = await new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = paths.BootstrapFile, Pooling = false }.ToString())
                .ExecuteScalarAsync<string?>("SELECT v FROM bootstrap WHERE k = 'setup_complete';") == "true";
            logger.LogWarning("Restored the data folder from {File}; the previous files are in {Aside}", fileName, aside);
            return new(true, complete
                ? "Restored. This backup had finished setup, so PayLink is ready: sign in to /admin."
                : "Restored. Setup continues where this backup left off.", complete);
        }
        finally
        {
            try { Directory.Delete(work, true); } catch (IOException) { }
        }
    }

    private static bool IsSqlite(byte[] header) => header.Length >= 16 && System.Text.Encoding.ASCII.GetString(header, 0, 15) == "SQLite format 3";

    /// <summary>Extracts only the names a PayLink backup contains; anything else (or a path that escapes) is refused.</summary>
    private static string? ExtractBackup(string zipFile, string staged)
    {
        try
        {
            using var zip = ZipFile.OpenRead(zipFile);
            foreach (var e in zip.Entries)
            {
                if (e.FullName.EndsWith('/')) continue;
                var name = e.FullName.Replace('\\', '/');
                var allowed = name is "bootstrap.db" or "platform.db" or "paylink-backup.json" or "keys/master.key"
                              || (name.StartsWith("tenants/", StringComparison.Ordinal) && name.EndsWith(".db", StringComparison.Ordinal) && name.Count(ch => ch == '/') == 1)
                              || name.StartsWith("files/", StringComparison.Ordinal);
                if (!allowed || name.Contains("..")) return $"The backup contains {name}, which isn't part of a PayLink backup.";
                if (name == "paylink-backup.json") continue;
                var target = Path.GetFullPath(Path.Combine(staged, name));
                if (!target.StartsWith(Path.GetFullPath(staged) + Path.DirectorySeparatorChar, StringComparison.Ordinal)) return "The backup has an unsafe path.";
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                e.ExtractToFile(target);
            }
            return null;
        }
        catch (InvalidDataException) { return "The .zip file is damaged."; }
    }

    /// <summary>A real PayLink bootstrap.db whose stored secrets the key it comes with (or PayLink__MasterKey) can read.</summary>
    private string? CheckBootstrap(string bootstrapFile, string staged)
    {
        var header = new byte[16];
        using (var f = File.OpenRead(bootstrapFile)) _ = f.Read(header, 0, 16);
        if (!IsSqlite(header)) return "bootstrap.db isn't a SQLite database.";
        string? platformEnc;
        try
        {
            using var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = bootstrapFile, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
            c.Open();
            if (c.ExecuteScalar<long>("SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'bootstrap';") == 0)
                return "That SQLite file isn't a PayLink bootstrap.db (it has no bootstrap table).";
            platformEnc = c.ExecuteScalar<string?>("SELECT v FROM bootstrap WHERE k = 'platform_db';");
        }
        catch (SqliteException ex) { return "bootstrap.db can't be read: " + ex.Message; }

        byte[]? key = secrets.Source == "environment" ? null : SecretProtector.ReadKey(bootstrapFile);
        var legacy = Path.Combine(staged, "keys", "master.key");
        if (key is null && secrets.Source != "environment" && File.Exists(legacy)) key = Convert.FromBase64String(File.ReadAllText(legacy).Trim());
        if (platformEnc is null) return null;   // nothing encrypted yet
        if (secrets.Source == "environment")
            return platformEnc is not null && !CanReadWithCurrent(platformEnc) ? "This backup was encrypted with a different key than PayLink__MasterKey." : null;
        if (key is null) return "This bootstrap.db has no encryption key inside (it came from an older PayLink). Upload the backup .zip with keys/master.key, or set PayLink__MasterKey.";
        return SecretProtector.CanRead(key, platformEnc) ? null : "The encryption key in this backup can't read its own secrets; the file may be damaged.";
    }

    private bool CanReadWithCurrent(string value)
    {
        try { secrets.Unprotect(value); return true; }
        catch (Exception) { return false; }
    }
}
