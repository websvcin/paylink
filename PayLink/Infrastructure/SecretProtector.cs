using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace PayLink.Infrastructure;

/// <summary>
/// Encrypts secrets stored in databases (connection strings, client secrets, hosting passwords) with AES-256-GCM.
/// Like LoginLink, the key lives inside bootstrap.db by default, so the data folder is self-contained: back up or move
/// bootstrap.db and every secret it protects comes with it. Set PayLink__MasterKey (base64, 32 bytes) to keep the key
/// outside the data folder instead. A keys/master.key file from an earlier version is imported once.
/// </summary>
public sealed class SecretProtector
{
    private const string Prefix = "v1:";
    private const string KeyName = "master";
    private readonly AppPaths? _paths;
    private readonly string? _configured;
    private byte[] _key = [];

    public SecretProtector(AppPaths paths, IConfiguration configuration)
    {
        _paths = paths;
        _configured = configuration["PayLink:MasterKey"];
        Reload();
    }

    internal SecretProtector(byte[] key) => _key = key;

    /// <summary>Where the key comes from: "environment" (PayLink__MasterKey) or "bootstrap" (inside bootstrap.db).</summary>
    public string Source { get; private set; } = "bootstrap";

    /// <summary>The first characters of the key's SHA-256: lets people tell keys apart without revealing them.</summary>
    public string Fingerprint => Convert.ToHexString(SHA256.HashData(_key))[..12].ToLowerInvariant();

    /// <summary>Re-reads the key, e.g. after a bootstrap.db was restored from a backup.</summary>
    public void Reload()
    {
        if (!string.IsNullOrWhiteSpace(_configured))
        {
            _key = Convert.FromBase64String(_configured);
            if (_key.Length != 32) throw new InvalidOperationException("PayLink:MasterKey must be 32 bytes, base64-encoded.");
            Source = "environment";
            return;
        }
        Source = "bootstrap";
        _key = ReadOrCreateKey(_paths!);
    }

    /// <summary>Reads the key stored in a bootstrap.db file, or null if it has none (used to check an uploaded backup).</summary>
    public static byte[]? ReadKey(string bootstrapFile)
    {
        using var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = bootstrapFile, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        c.Open();
        using var exists = c.CreateCommand();
        exists.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'secret_keys';";
        if (Convert.ToInt64(exists.ExecuteScalar()) == 0) return null;
        using var read = c.CreateCommand();
        read.CommandText = "SELECT value FROM secret_keys WHERE name = $name;";
        read.Parameters.AddWithValue("$name", KeyName);
        return read.ExecuteScalar() is string s ? Convert.FromBase64String(s) : null;
    }

    /// <summary>True when this key can read a value protected with <see cref="Protect"/>.</summary>
    public static bool CanRead(byte[] key, string protectedValue)
    {
        try { _ = new SecretProtector(key).Unprotect(protectedValue); return true; }
        catch (Exception ex) when (ex is CryptographicException or FormatException or ArgumentException) { return false; }
    }

    private static byte[] ReadOrCreateKey(AppPaths paths)
    {
        Directory.CreateDirectory(paths.DataRoot);
        using var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = paths.BootstrapFile, Pooling = false }.ToString());
        c.Open();
        using (var create = c.CreateCommand())
        {
            create.CommandText = "CREATE TABLE IF NOT EXISTS secret_keys (name VARCHAR(50) PRIMARY KEY, value TEXT NOT NULL, created_at VARCHAR(40) NOT NULL);";
            create.ExecuteNonQuery();
        }
        using (var read = c.CreateCommand())
        {
            read.CommandText = "SELECT value FROM secret_keys WHERE name = $name;";
            read.Parameters.AddWithValue("$name", KeyName);
            if (read.ExecuteScalar() is string stored) return Convert.FromBase64String(stored);
        }

        // An earlier PayLink kept the key in keys/master.key: import it, so existing secrets stay readable.
        var legacy = Path.Combine(paths.KeysDirectory, "master.key");
        var key = File.Exists(legacy) ? Convert.FromBase64String(File.ReadAllText(legacy).Trim()) : RandomNumberGenerator.GetBytes(32);
        using (var insert = c.CreateCommand())
        {
            insert.CommandText = "INSERT INTO secret_keys (name, value, created_at) VALUES ($name, $value, $at);";
            insert.Parameters.AddWithValue("$name", KeyName);
            insert.Parameters.AddWithValue("$value", Convert.ToBase64String(key));
            insert.Parameters.AddWithValue("$at", Clock.Now());
            insert.ExecuteNonQuery();
        }
        if (File.Exists(legacy))
        {
            try { File.Move(legacy, legacy + ".imported", overwrite: true); } catch (IOException) { }
        }
        return key;
    }

    public string Protect(string plaintext)
    {
        var nonce = RandomNumberGenerator.GetBytes(12);
        var plain = Encoding.UTF8.GetBytes(plaintext);
        var cipher = new byte[plain.Length];
        var tag = new byte[16];
        using var aes = new AesGcm(_key, 16);
        aes.Encrypt(nonce, plain, cipher, tag);
        return Prefix + Convert.ToBase64String([.. nonce, .. cipher, .. tag]);
    }

    public string Unprotect(string protectedValue)
    {
        if (!protectedValue.StartsWith(Prefix, StringComparison.Ordinal))
            throw new CryptographicException("Unknown secret format.");
        var data = Convert.FromBase64String(protectedValue[Prefix.Length..]);
        var nonce = data.AsSpan(0, 12);
        var tag = data.AsSpan(data.Length - 16);
        var cipher = data.AsSpan(12, data.Length - 28);
        var plain = new byte[cipher.Length];
        using var aes = new AesGcm(_key, 16);
        aes.Decrypt(nonce, cipher, tag, plain);
        return Encoding.UTF8.GetString(plain);
    }

    public string? ProtectOrNull(string? plaintext) => string.IsNullOrEmpty(plaintext) ? null : Protect(plaintext);
    public string? UnprotectOrNull(string? value) => string.IsNullOrEmpty(value) ? null : Unprotect(value);

    public string ProtectJson<T>(T value) => Protect(JsonSerializer.Serialize(value, Json.Options));
    public T? UnprotectJson<T>(string? value) => string.IsNullOrEmpty(value) ? default : JsonSerializer.Deserialize<T>(Unprotect(value), Json.Options);
}

public static class Json
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };
}
