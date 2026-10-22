namespace PayLink.Infrastructure;

/// <summary>Shows that a secret is saved without showing the secret: dots, plus the last four characters of a long key.</summary>
public static class Masking
{
    public static string Mask(string? secret) =>
        string.IsNullOrEmpty(secret) ? "" : "••••••••••" + (secret.Length >= 16 ? secret[^4..] : "");

    /// <summary>For a secret field left blank on purpose: the masked saved value, or that nothing is saved yet.</summary>
    public static string Placeholder(string? saved) =>
        string.IsNullOrEmpty(saved) ? "Not saved yet" : $"{Mask(saved)}   saved; type to replace";
}
