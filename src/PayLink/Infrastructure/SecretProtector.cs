using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PayLink.Infrastructure;

/// <summary>
/// Encrypts secrets stored in databases (connection strings, client secrets, hosting passwords) with AES-256-GCM.
/// The master key is App_Data/keys/master.key, or the PayLink:MasterKey setting (base64, 32 bytes) when provided.
/// Losing the key means losing every stored secret, so it belongs in the recovery kit.
/// </summary>
public sealed class SecretProtector
{
    private const string Prefix = "v1:";
    private readonly byte[] _key;

    public SecretProtector(AppPaths paths, IConfiguration configuration)
    {
        var configured = configuration["PayLink:MasterKey"];
        if (!string.IsNullOrWhiteSpace(configured))
        {
            _key = Convert.FromBase64String(configured);
            if (_key.Length != 32) throw new InvalidOperationException("PayLink:MasterKey must be 32 bytes, base64-encoded.");
            return;
        }

        var file = Path.Combine(paths.KeysDirectory, "master.key");
        if (File.Exists(file))
        {
            _key = Convert.FromBase64String(File.ReadAllText(file).Trim());
        }
        else
        {
            _key = RandomNumberGenerator.GetBytes(32);
            File.WriteAllText(file, Convert.ToBase64String(_key));
            TryRestrict(file);
        }
    }

    internal SecretProtector(byte[] key) => _key = key;

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

    private static void TryRestrict(string file)
    {
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }
}

public static class Json
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };
}
