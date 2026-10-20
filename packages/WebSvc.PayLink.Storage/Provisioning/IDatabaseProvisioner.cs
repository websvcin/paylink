using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace WebSvc.PayLink.Storage.Provisioning;

/// <summary>A database created on demand, with its own user. <see cref="ProviderReference"/> lets the provisioner remove it again.</summary>
public sealed record ProvisionedDatabase(
    string DatabaseName,
    string UserName,
    DatabaseTarget Target,
    string ProviderReference);

public sealed record ProvisioningCheck(bool Ok, string Message);

public sealed class ProvisioningException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>Creates and removes MySQL databases on a hosting environment.</summary>
public interface IDatabaseProvisioner
{
    HostingKind Kind { get; }

    /// <summary>Confirms PayLink can reach the environment and create a database, by creating and removing a throwaway one.</summary>
    Task<ProvisioningCheck> TestAsync(CancellationToken ct = default);

    /// <summary>Creates a database named <c>{prefix}{label}_{random}</c> with its own user and generated password.</summary>
    Task<ProvisionedDatabase> CreateAsync(string prefix, string label, CancellationToken ct = default);

    /// <summary>Removes a database (and its user) created by <see cref="CreateAsync"/>. Used to clean up after a failed attempt.</summary>
    Task DropAsync(ProvisionedDatabase database, CancellationToken ct = default);
}

public static partial class ProvisioningNames
{
    [GeneratedRegex("[^a-z0-9_]")]
    private static partial Regex Unsafe();

    /// <summary>A safe MySQL identifier: lowercase letters, digits and underscores only, at most <paramref name="maxLength"/> characters.</summary>
    public static string DatabaseName(string prefix, string label, int maxLength = 64)
    {
        var cleanPrefix = Unsafe().Replace(prefix.ToLowerInvariant(), "_");
        var cleanLabel = Unsafe().Replace(label.ToLowerInvariant(), "_").Trim('_');
        var suffix = "_" + RandomToken(4);
        var room = Math.Max(1, maxLength - cleanPrefix.Length - suffix.Length);
        if (cleanLabel.Length > room) cleanLabel = cleanLabel[..room];
        return cleanPrefix + cleanLabel + suffix;
    }

    /// <summary>MySQL 8 allows 32-character user names; cPanel prefixes them with the account name.</summary>
    public static string UserName(string prefix = "pl_") => prefix + RandomToken(8);

    public static string Password() => RandomToken(28, includeUpper: true);

    public static string RandomToken(int length, bool includeUpper = false)
    {
        var alphabet = includeUpper ? "abcdefghijkmnopqrstuvwxyzABCDEFGHJKLMNPQRSTUVWXYZ23456789" : "abcdefghijkmnopqrstuvwxyz0123456789";
        return RandomNumberGenerator.GetString(alphabet, length);
    }

    public static void EnsureSafeIdentifier(string value, string what)
    {
        if (string.IsNullOrEmpty(value) || Unsafe().IsMatch(value) || value.Length > 64)
            throw new ProvisioningException($"Unsafe {what}: only lowercase letters, digits and underscores are allowed.");
    }
}

public static class DatabaseProvisioners
{
    /// <summary>Builds the provisioner for the configured hosting environment, or null when none is set up.</summary>
    public static IDatabaseProvisioner? Create(HostingSettings settings, HttpClient http) => settings.Kind switch
    {
        HostingKind.MySqlServer when settings.MySqlServer is not null => new MySqlServerProvisioner(settings.MySqlServer, settings.ConnectionHostOverride),
        HostingKind.Plesk when settings.Plesk is not null => new PleskProvisioner(settings.Plesk, http, settings.ConnectionHostOverride),
        HostingKind.CPanel when settings.CPanel is not null => new CPanelProvisioner(settings.CPanel, http, settings.ConnectionHostOverride),
        _ => null,
    };
}
