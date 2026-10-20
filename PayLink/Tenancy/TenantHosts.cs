using System.Collections.Concurrent;
using PayLink.Platform;

namespace PayLink.Tenancy;

/// <summary>
/// Serves a tenant's console on its own host: {slug}.{tenant domain} for tenants switched to subdomain mode
/// (Admin → Tenants → tenant). Paths are rewritten onto the usual /{slug}/… routes, so "/" is the console, "/login" its
/// sign-in, and "/{slug}/console/…" links keep working. Shared paths (checkout, API, hooks, sign-in callback, assets)
/// pass through unchanged. Needs wildcard DNS and a wildcard TLS certificate for the domain.
/// </summary>
public sealed class TenantHosts(RequestDelegate next)
{
    public const string HostItem = "paylink.tenant_host";
    private static readonly string[] Shared = ["/pay/", "/hooks/", "/api/", "/auth/", "/css/", "/js/", "/docs/", "/healthz", "/favicon", "/l/", "/dropin", "/setup", "/admin"];
    private static readonly ConcurrentDictionary<string, (string? Slug, DateTime At)> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static void Forget() => Cache.Clear();

    public async Task InvokeAsync(HttpContext context, PlatformSettings settings, PlatformRepository repo, PayLink.Infrastructure.BootstrapStore bootstrap)
    {
        var host = context.Request.Host.Host;
        if (!await bootstrap.IsSetupCompleteAsync()) { await next(context); return; }
        if (!Cache.TryGetValue(host, out var hit) || DateTime.UtcNow - hit.At > TimeSpan.FromSeconds(30))
        {
            hit = (await ResolveAsync(host, settings, repo), DateTime.UtcNow);
            Cache[host] = hit;
        }
        if (hit.Slug is { } slug)
        {
            context.Items[HostItem] = slug;
            var path = context.Request.Path.Value ?? "/";
            if (path == "/") context.Request.Path = $"/{slug}/console";
            else if (!path.StartsWith($"/{slug}/", StringComparison.OrdinalIgnoreCase) && !Shared.Any(p => path.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
                context.Request.Path = $"/{slug}{path}";
        }
        await next(context);
    }

    private static async Task<string?> ResolveAsync(string host, PlatformSettings settings, PlatformRepository repo)
    {
        var domain = await settings.GetTenantDomainAsync();
        if (domain.Length == 0 || !host.EndsWith("." + domain, StringComparison.OrdinalIgnoreCase)) return null;
        var slug = host[..^(domain.Length + 1)].ToLowerInvariant();
        if (slug.Contains('.')) return null;
        var tenant = await repo.TenantBySlugAsync(slug);
        return tenant is { ConsoleMode: "subdomain" } ? tenant.Slug : null;
    }
}
