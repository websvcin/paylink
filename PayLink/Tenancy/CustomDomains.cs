using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Dapper;
using DnsClient;
using PayLink.Identity;
using PayLink.Infrastructure;
using PayLink.Payments;
using PayLink.Platform;

namespace PayLink.Tenancy;

public sealed class TenantDomain
{
    public string Domain { get; set; } = "";
    public long TenantId { get; set; }
    public string Token { get; set; } = "";
    public string Status { get; set; } = CustomDomains.Pending;
    public string? VerifiedAt { get; set; }
    public string? CheckedAt { get; set; }
    public string? LastError { get; set; }
    public string CreatedAt { get; set; } = "";
    public string? CreatedBy { get; set; }

    public bool Verified => Status == CustomDomains.VerifiedStatus;
    /// <summary>The DNS name that must hold the TXT record.</summary>
    public string ChallengeName => $"{CustomDomains.ChallengePrefix}.{Domain}";
    public string ChallengeValue => $"paylink-verify={Token}";
}

/// <summary>Looks up TXT records. An interface so tests (and offline installs) can answer without DNS.</summary>
public interface IDnsTxtResolver
{
    Task<IReadOnlyList<string>> TxtAsync(string name, CancellationToken ct = default);
}

public sealed class DnsTxtResolver : IDnsTxtResolver
{
    private readonly LookupClient _dns = new(new LookupClientOptions { UseCache = false, Timeout = TimeSpan.FromSeconds(5), Retries = 1 });

    public async Task<IReadOnlyList<string>> TxtAsync(string name, CancellationToken ct = default)
    {
        var result = await _dns.QueryAsync(name, QueryType.TXT, cancellationToken: ct);
        return result.Answers.TxtRecords().Select(r => string.Concat(r.Text)).ToList();
    }
}

/// <summary>
/// A tenant's own domain, such as pay.acme.in. The tenant proves it with a TXT record, then points the name at PayLink
/// (CNAME or A record). Once verified, the console answers there like a subdomain console, payment and link URLs use it,
/// and LoginLink accepts its sign-in callback. TLS is the reverse proxy's job; /.well-known/paylink/domain lets one
/// that issues certificates on demand (Caddy's "ask") check the name first.
/// </summary>
public sealed partial class CustomDomains(PlatformDb platformDb, PlatformSettings settings, IDnsTxtResolver dns, ConsoleCallbacks callbacks)
{
    public const string Pending = "pending";
    public const string VerifiedStatus = "verified";
    public const string ChallengePrefix = "_paylink-challenge";

    private readonly ConcurrentDictionary<long, (string? BaseUrl, DateTime At)> _baseCache = new();

    [GeneratedRegex(@"^(?=.{4,253}$)([a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?\.)+[a-z]{2,63}$")]
    private static partial Regex DomainPattern();

    public static string Normalize(string? domain) =>
        (domain ?? "").Trim().ToLowerInvariant().Replace("https://", "").Replace("http://", "").TrimEnd('/', '.');

    public async Task<TenantDomain?> ForTenantAsync(long tenantId)
    {
        await using var c = await platformDb.OpenAsync();
        return await c.QueryFirstOrDefaultAsync<TenantDomain>("SELECT * FROM tenant_domains WHERE tenant_id = @tenantId;", new { tenantId });
    }

    /// <summary>Starts (or restarts) verification of a domain for the tenant. A tenant has one domain; adding another replaces it.</summary>
    public async Task<(TenantDomain? Domain, string? Error)> AddAsync(TenantRecord tenant, string? input, Actor actor)
    {
        var domain = Normalize(input);
        if (!DomainPattern().IsMatch(domain)) return (null, "Enter a domain name such as pay.acme.in.");
        var platformHost = new Uri((await settings.GetPublicBaseUrlAsync()) is { Length: > 0 } b ? b : "http://localhost").Host;
        var tenantDomain = await settings.GetTenantDomainAsync();
        if (domain == platformHost || (tenantDomain.Length > 0 && (domain == tenantDomain || domain.EndsWith("." + tenantDomain))))
            return (null, "That name belongs to the platform. Use a domain of your own.");
        await using var c = await platformDb.OpenAsync();
        var owner = await c.QueryFirstOrDefaultAsync<TenantDomain>("SELECT * FROM tenant_domains WHERE domain = @domain;", new { domain });
        if (owner is not null && owner.TenantId != tenant.Id) return (null, $"{domain} is already in use on this platform.");
        await c.ExecuteAsync("DELETE FROM tenant_domains WHERE tenant_id = @Id;", tenant);
        var row = new TenantDomain
        {
            Domain = domain, TenantId = tenant.Id, Token = Ids.New("x", 24)[2..].ToLowerInvariant(), Status = Pending, CreatedAt = Clock.Now(), CreatedBy = actor.Name,
        };
        await c.ExecuteAsync("INSERT INTO tenant_domains (domain, tenant_id, token, status, created_at, created_by) VALUES (@Domain, @TenantId, @Token, @Status, @CreatedAt, @CreatedBy);", row);
        await AuditLog.WriteAsync(c, actor, "tenant.domain_added", tenant.Slug, domain);
        Forget();
        return (row, null);
    }

    /// <summary>Checks the TXT record. On success the domain goes live and LoginLink is told about its callback.</summary>
    public async Task<(bool Ok, string Message)> VerifyAsync(TenantRecord tenant, Actor actor, CancellationToken ct = default)
    {
        if (await ForTenantAsync(tenant.Id) is not { } d) return (false, "Add a domain first.");
        string? error = null;
        try
        {
            var values = await dns.TxtAsync(d.ChallengeName, ct);
            if (!values.Any(v => v.Trim() == d.ChallengeValue))
                error = values.Count == 0
                    ? $"No TXT record at {d.ChallengeName} yet. DNS changes can take a few minutes to show."
                    : $"The TXT record at {d.ChallengeName} doesn't hold {d.ChallengeValue}.";
        }
        catch (Exception ex) when (ex is DnsResponseException or OperationCanceledException or System.Net.Sockets.SocketException)
        {
            error = $"DNS didn't answer for {d.ChallengeName}: {ex.Message}";
        }
        await using var c = await platformDb.OpenAsync();
        if (error is not null)
        {
            await c.ExecuteAsync("UPDATE tenant_domains SET checked_at = @at, last_error = @error WHERE domain = @Domain;", new { at = Clock.Now(), error, d.Domain });
            return (false, error);
        }
        await c.ExecuteAsync("UPDATE tenant_domains SET status = @verified, verified_at = @at, checked_at = @at, last_error = NULL WHERE domain = @Domain;",
            new { verified = VerifiedStatus, at = Clock.Now(), d.Domain });
        await AuditLog.WriteAsync(c, actor, "tenant.domain_verified", tenant.Slug, d.Domain);
        Forget();
        var (_, note) = await callbacks.RegisterAsync(tenant);
        return (true, $"{d.Domain} is verified. Point it at PayLink if you haven't, and your console, links and checkout answer there.{note}");
    }

    public async Task RemoveAsync(TenantRecord tenant, Actor actor)
    {
        await using (var c = await platformDb.OpenAsync())
        {
            var n = await c.ExecuteAsync("DELETE FROM tenant_domains WHERE tenant_id = @Id;", tenant);
            if (n == 0) return;
            await AuditLog.WriteAsync(c, actor, "tenant.domain_removed", tenant.Slug);
        }
        Forget();
        await callbacks.RegisterAsync(tenant);
    }

    /// <summary>The tenant slug a verified domain belongs to, for <see cref="TenantHosts"/>.</summary>
    public async Task<string?> SlugForHostAsync(string host, PlatformRepository repo)
    {
        await using var c = await platformDb.OpenAsync();
        var tenantId = await c.ExecuteScalarAsync<long?>("SELECT tenant_id FROM tenant_domains WHERE domain = @host AND status = @verified;",
            new { host = host.ToLowerInvariant(), verified = VerifiedStatus });
        return tenantId is { } id && await repo.TenantByIdAsync(id) is { IsActive: true } t ? t.Slug : null;
    }

    public async Task<bool> IsVerifiedAsync(string domain)
    {
        await using var c = await platformDb.OpenAsync();
        return await c.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM tenant_domains WHERE domain = @domain AND status = @verified;",
            new { domain = Normalize(domain), verified = VerifiedStatus }) > 0;
    }

    /// <summary>Where the tenant's payers are sent: https://{its domain} once verified, else the platform's address.</summary>
    public async Task<string> BaseUrlAsync(TenantRecord tenant)
    {
        if (!_baseCache.TryGetValue(tenant.Id, out var hit) || DateTime.UtcNow - hit.At > TimeSpan.FromSeconds(30))
        {
            var d = await ForTenantAsync(tenant.Id);
            hit = (d is { Verified: true } ? $"https://{d.Domain}" : null, DateTime.UtcNow);
            _baseCache[tenant.Id] = hit;
        }
        return hit.BaseUrl ?? await settings.GetPublicBaseUrlAsync();
    }

    private void Forget()
    {
        _baseCache.Clear();
        TenantHosts.Forget();
    }
}

/// <summary>
/// The sign-in callbacks a tenant's console uses (the platform address, its subdomain, its own domain), registered on the
/// PayLink console app in the tenant's LoginLink organization. LoginLink replaces the whole list, so it's always sent complete.
/// </summary>
public sealed class ConsoleCallbacks(PlatformSettings settings, PlatformDb platformDb, LoginLinkClient loginLink, SecretProtector secrets)
{
    public async Task<IReadOnlyList<string>> UrlsAsync(TenantRecord tenant)
    {
        var baseUrl = await settings.GetPublicBaseUrlAsync();
        var urls = new List<string> { $"{baseUrl}/auth/callback" };
        var tenantDomain = await settings.GetTenantDomainAsync();
        if (tenant.ConsoleMode == "subdomain" && tenantDomain.Length > 0)
            urls.Add($"{(baseUrl.StartsWith("http://") ? "http" : "https")}://{tenant.Slug}.{tenantDomain}/auth/callback");
        await using var c = await platformDb.OpenAsync();
        if (await c.ExecuteScalarAsync<string?>("SELECT domain FROM tenant_domains WHERE tenant_id = @Id AND status = @verified;",
                new { tenant.Id, verified = CustomDomains.VerifiedStatus }) is { } domain)
            urls.Add($"https://{domain}/auth/callback");
        return urls;
    }

    /// <summary>Registers the callbacks; returns a sentence for the person when PayLink couldn't do it itself.</summary>
    public async Task<(bool Ok, string Note)> RegisterAsync(TenantRecord tenant)
    {
        var urls = await UrlsAsync(tenant);
        if (tenant.MgmtClientId is not { } id || tenant.MgmtClientSecretEnc is not { } enc || tenant.ConsoleAppId is not { Length: > 0 } appId
            || await settings.GetLoginLinkAsync() is not { } ll)
            return (false, $" PayLink has no management credential for {tenant.LoginlinkOrg}: add {string.Join(", ", urls.Skip(1))} to the PayLink console app's redirect URIs in LoginLink.");
        try
        {
            var config = await loginLink.GetDiscoveryAsync(LoginLinkClient.DiscoveryUrl(ll.ServerUrl, tenant.LoginlinkOrg));
            var token = await loginLink.ClientCredentialsTokenAsync(config.TokenEndpoint, id, secrets.Unprotect(enc));
            await loginLink.SetAppRedirectUrisAsync(ll.ServerUrl, token, appId, urls);
            return (true, " LoginLink accepts its sign-in callback.");
        }
        catch (LoginLinkException ex)
        {
            return (false, " LoginLink didn't accept the new sign-in callback: " + ex.Message);
        }
    }
}
