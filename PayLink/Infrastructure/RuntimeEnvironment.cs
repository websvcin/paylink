using System.Globalization;
using System.Net;

namespace PayLink.Infrastructure;

/// <summary>Facts about where PayLink is running, used to give setup advice that fits (Docker, Windows, Linux).</summary>
public static class RuntimeEnvironment
{
    /// <summary>The official .NET container images set DOTNET_RUNNING_IN_CONTAINER=true.</summary>
    public static bool InContainer =>
        string.Equals(Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER"), "true", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Whether <paramref name="path"/> sits on a mount (e.g. a Docker volume or bind mount) rather than in the container's own layer.
    /// Linux only; null when it can't be told.
    /// </summary>
    public static bool? IsOnMountedVolume(string path)
    {
        const string mountInfo = "/proc/self/mountinfo";
        if (!OperatingSystem.IsLinux() || !File.Exists(mountInfo)) return null;
        try
        {
            var full = Path.GetFullPath(path).TrimEnd('/');
            foreach (var line in File.ReadLines(mountInfo))
            {
                // Field 5 is the mount point; octal escapes such as \040 stand for spaces.
                var parts = line.Split(' ');
                if (parts.Length < 5) continue;
                var mountPoint = parts[4].Replace("\\040", " ").TrimEnd('/');
                if (mountPoint.Length == 0) continue;   // the root filesystem doesn't count
                if (full == mountPoint || full.StartsWith(mountPoint + "/", StringComparison.Ordinal))
                {
                    if (mountPoint is "/proc" or "/sys" or "/dev") continue;
                    return true;
                }
            }
            return false;
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    /// <summary>
    /// The default gateway seen from inside a container: the Docker host (usually 172.17.0.1 on the default bridge).
    /// A database or panel on the same server is reached at this address, not at "localhost". Linux only.
    /// </summary>
    public static string? DockerHostAddress()
    {
        const string routes = "/proc/net/route";
        if (!InContainer || !File.Exists(routes)) return null;
        try
        {
            foreach (var line in File.ReadLines(routes).Skip(1))
            {
                var cols = line.Split('\t', StringSplitOptions.RemoveEmptyEntries);
                if (cols.Length < 3 || cols[1] != "00000000") continue;   // default route
                var gateway = uint.Parse(cols[2], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                return new IPAddress(gateway).ToString();   // stored little-endian, which IPAddress(long) expects
            }
        }
        catch (Exception ex) when (ex is IOException or FormatException or OverflowException) { }
        return null;
    }

    /// <summary>A request made on the PayLink server itself, not through a reverse proxy.</summary>
    public static bool IsLocalRequest(HttpContext http) =>
        http.Connection.RemoteIpAddress is { } ip && IPAddress.IsLoopback(ip)
        && !http.Request.Headers.ContainsKey("X-Forwarded-For")
        && !http.Request.Headers.ContainsKey("Forwarded");
}
