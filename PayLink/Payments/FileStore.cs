using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using PayLink.Infrastructure;
using PayLink.Platform;

namespace PayLink.Payments;

/// <summary>Where PayLink keeps files: the data folder (default) or a FileLink server. Chosen in Admin → Settings.</summary>
public sealed class FileStorageSettings
{
    public const string Local = "local";
    public const string FileLink = "filelink";

    public string Kind { get; set; } = Local;
    public string? Server { get; set; }
    public string? Key { get; set; }
}

/// <summary>A place file bytes live. A stored reference names its backend, so files stay readable after the setting changes.</summary>
public interface IFileBackend
{
    Task<string> PutAsync(string path, Stream content, string contentType, CancellationToken ct = default);
    Task<(Stream Content, string ContentType)?> OpenAsync(string reference, CancellationToken ct = default);
}

/// <summary>Files under {data folder}/files. References are relative paths such as 12/2026-10/ab12….png.</summary>
public sealed class LocalFileBackend(AppPaths paths) : IFileBackend
{
    public string Root => Path.Combine(paths.DataRoot, "files");

    public async Task<string> PutAsync(string path, Stream content, string contentType, CancellationToken ct = default)
    {
        var full = Resolve(path) ?? throw new InvalidOperationException("Bad file path.");
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        await using var target = File.Create(full);
        await content.CopyToAsync(target, ct);
        return path;
    }

    public Task<(Stream Content, string ContentType)?> OpenAsync(string reference, CancellationToken ct = default) =>
        Task.FromResult(Resolve(reference) is { } full && File.Exists(full)
            ? ((Stream)File.OpenRead(full), FileStore.ContentType(full))
            : ((Stream, string)?)null);

    /// <summary>The full path for a stored file, or null if the path tries to leave the files folder.</summary>
    public string? Resolve(string relative)
    {
        var root = Path.GetFullPath(Root) + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        return full.StartsWith(root, StringComparison.Ordinal) ? full : null;
    }
}

/// <summary>
/// A FileLink server, through the small contract in https://websvcin.github.io/paylink/self-hosting/filelink-contract: PUT a file, GET it back, a health check.
/// References are "filelink:{id}". Bytes go server to server; nothing in the browser holds a FileLink key.
/// </summary>
public sealed class FileLinkBackend(HttpClient http, string server, string key) : IFileBackend
{
    public const string Prefix = "filelink:";
    public const string HttpClientName = "filelink";

    private HttpRequestMessage Request(HttpMethod method, string path)
    {
        var r = new HttpRequestMessage(method, server.TrimEnd('/') + path);
        r.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        return r;
    }

    public async Task<string?> CheckAsync(CancellationToken ct = default)
    {
        try
        {
            using var response = await http.SendAsync(Request(HttpMethod.Get, "/api/v1/health"), ct);
            return response.IsSuccessStatusCode ? null : $"FileLink answered {(int)response.StatusCode}{(response.StatusCode == System.Net.HttpStatusCode.Unauthorized ? ": the key wasn't accepted" : "")}.";
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or UriFormatException)
        {
            return "PayLink couldn't reach FileLink: " + ex.Message;
        }
    }

    public async Task<string> PutAsync(string path, Stream content, string contentType, CancellationToken ct = default)
    {
        using var request = Request(HttpMethod.Put, "/api/v1/files/" + string.Join('/', path.Split('/').Select(Uri.EscapeDataString)));
        request.Content = new StreamContent(content);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        using var response = await http.SendAsync(request, ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode) throw new IOException($"FileLink didn't store the file ({(int)response.StatusCode}).");
        var id = (string?)JsonNode.Parse(text)?["id"] ?? throw new IOException("FileLink didn't say where it stored the file.");
        return Prefix + id;
    }

    public async Task<(Stream Content, string ContentType)?> OpenAsync(string reference, CancellationToken ct = default)
    {
        var response = await http.SendAsync(Request(HttpMethod.Get, "/api/v1/files/" + Uri.EscapeDataString(reference[Prefix.Length..])), HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode) { response.Dispose(); return null; }
        return (await response.Content.ReadAsStreamAsync(ct), response.Content.Headers.ContentType?.MediaType ?? "application/octet-stream");
    }
}

/// <summary>
/// Files payers upload (UPI payment screenshots). Checks them (size, real image type), then stores them where Admin → Settings says:
/// the data folder by default, or FileLink. Callers keep only the reference it returns.
/// </summary>
public sealed class FileStore(AppPaths paths, PlatformSettings settings, SecretProtector secrets, IHttpClientFactory httpFactory)
{
    public const long MaxBytes = 5 * 1024 * 1024;
    private const string SettingKey = "file_storage";

    private readonly LocalFileBackend _local = new(paths);

    public string Root => _local.Root;

    public async Task<FileStorageSettings> GetSettingsAsync() =>
        secrets.UnprotectJson<FileStorageSettings>(await settings.GetAsync(SettingKey)) ?? new FileStorageSettings();

    public FileLinkBackend FileLink(string server, string key) => new(httpFactory.CreateClient(FileLinkBackend.HttpClientName), server, key);

    /// <summary>Saves the choice; FileLink is checked first and isn't saved if it doesn't answer.</summary>
    public async Task<string?> SaveSettingsAsync(FileStorageSettings next)
    {
        if (next.Kind == FileStorageSettings.FileLink)
        {
            var saved = await GetSettingsAsync();
            if (string.IsNullOrEmpty(next.Key)) next.Key = saved.Key;
            if (!Uri.TryCreate(next.Server, UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http")) return "Enter FileLink's address, e.g. https://files.example.in.";
            if (string.IsNullOrEmpty(next.Key)) return "Enter a FileLink key.";
            if (await FileLink(next.Server!, next.Key).CheckAsync() is { } problem) return problem;
        }
        else
        {
            // Back to the data folder: keep FileLink's details so files already stored there can still be opened.
            var saved = await GetSettingsAsync();
            next = new FileStorageSettings { Kind = FileStorageSettings.Local, Server = saved.Server, Key = saved.Key };
        }
        await settings.SetAsync(SettingKey, secrets.ProtectJson(next));
        return null;
    }

    private async Task<IFileBackend> CurrentAsync() =>
        await GetSettingsAsync() is { Kind: FileStorageSettings.FileLink, Server: { } server, Key: { } key } ? FileLink(server, key) : _local;

    /// <summary>Stores an image and returns its reference, or a problem message. Only PNG, JPEG and WebP, checked by content.</summary>
    public async Task<(string? Path, string? Problem)> SaveImageAsync(long tenantId, IFormFile file)
    {
        if (file.Length == 0) return (null, null);
        if (file.Length > MaxBytes) return (null, "The screenshot can be at most 5 MB.");
        var head = new byte[12];
        await using (var s = file.OpenReadStream()) _ = await s.ReadAtLeastAsync(head, head.Length, throwOnEndOfStream: false);
        var ext = ImageExtension(head);
        if (ext is null) return (null, "Upload a PNG, JPEG or WebP screenshot.");

        var relative = $"{tenantId}/{DateTime.UtcNow:yyyy-MM}/{Guid.NewGuid():N}{ext}";
        try
        {
            await using var source = file.OpenReadStream();
            return (await (await CurrentAsync()).PutAsync(relative, source, ContentType(relative)), null);
        }
        catch (Exception ex) when (ex is IOException or HttpRequestException or TaskCanceledException)
        {
            return (null, "The screenshot couldn't be stored just now. Submit the UTR without it, or try again.");
        }
    }

    /// <summary>Opens a stored file from whichever backend holds it.</summary>
    public async Task<(Stream Content, string ContentType)?> OpenAsync(string reference)
    {
        if (!reference.StartsWith(FileLinkBackend.Prefix, StringComparison.Ordinal)) return await _local.OpenAsync(reference);
        var s = await GetSettingsAsync();
        if (s.Server is null || s.Key is null) return null;
        try { return await FileLink(s.Server, s.Key).OpenAsync(reference); }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) { return null; }
    }

    /// <summary>The full path for a file in the data folder, or null if the path tries to leave the files folder.</summary>
    public string? Resolve(string relative) => _local.Resolve(relative);

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
