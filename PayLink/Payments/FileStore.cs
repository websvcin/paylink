using PayLink.Infrastructure;

namespace PayLink.Payments;

/// <summary>
/// Files payers upload (UPI payment screenshots), kept in the data folder under files/{tenant id}/.
/// A FileLink-backed store can replace this later; callers only see a relative path.
/// </summary>
public sealed class FileStore(AppPaths paths)
{
    public const long MaxBytes = 5 * 1024 * 1024;

    public string Root => Path.Combine(paths.DataRoot, "files");

    /// <summary>Stores an image and returns its relative path, or a problem message. Only PNG, JPEG and WebP, checked by content.</summary>
    public async Task<(string? Path, string? Problem)> SaveImageAsync(long tenantId, IFormFile file)
    {
        if (file.Length == 0) return (null, null);
        if (file.Length > MaxBytes) return (null, "The screenshot can be at most 5 MB.");
        var head = new byte[12];
        await using (var s = file.OpenReadStream()) _ = await s.ReadAtLeastAsync(head, head.Length, throwOnEndOfStream: false);
        var ext = ImageExtension(head);
        if (ext is null) return (null, "Upload a PNG, JPEG or WebP screenshot.");

        var relative = $"{tenantId}/{DateTime.UtcNow:yyyy-MM}/{Guid.NewGuid():N}{ext}";
        var full = Resolve(relative)!;
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        await using (var target = File.Create(full))
        await using (var source = file.OpenReadStream())
            await source.CopyToAsync(target);
        return (relative, null);
    }

    /// <summary>The full path for a stored file, or null if the path tries to leave the files folder.</summary>
    public string? Resolve(string relative)
    {
        var root = System.IO.Path.GetFullPath(Root) + System.IO.Path.DirectorySeparatorChar;
        var full = System.IO.Path.GetFullPath(System.IO.Path.Combine(root, relative.Replace('/', System.IO.Path.DirectorySeparatorChar)));
        return full.StartsWith(root, StringComparison.Ordinal) ? full : null;
    }

    public static string ContentType(string path) => System.IO.Path.GetExtension(path) switch
    {
        ".png" => "image/png",
        ".webp" => "image/webp",
        _ => "image/jpeg",
    };

    private static string? ImageExtension(byte[] h) =>
        h[0] == 0x89 && h[1] == 0x50 && h[2] == 0x4E && h[3] == 0x47 ? ".png"
        : h[0] == 0xFF && h[1] == 0xD8 && h[2] == 0xFF ? ".jpg"
        : h[0] == 'R' && h[1] == 'I' && h[2] == 'F' && h[3] == 'F' && h[8] == 'W' && h[9] == 'E' && h[10] == 'B' && h[11] == 'P' ? ".webp"
        : null;
}
