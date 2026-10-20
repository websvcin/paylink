using System.Data.Common;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dapper;
using PayLink.Infrastructure;
using PayLink.Platform;
using PayLink.Tenancy;

namespace PayLink.Payments;

/// <summary>
/// Signed webhooks to the merchant's endpoints, with retries. Each delivery carries
/// <c>PayLink-Signature: t={unix seconds},v1={hex HMAC-SHA256 of "{t}.{body}" with the endpoint's secret}</c>.
/// Failed deliveries retry after 1 min, 5 min, 30 min, 2 h, 6 h, 12 h and 24 h, then stop.
/// </summary>
public sealed class WebhookService(TenantDb tenantDb, SecretProtector secrets, PlatformSettings platform, IHttpClientFactory httpFactory, ILogger<WebhookService> logger)
{
    public const string HttpClientName = "webhooks";
    public static readonly TimeSpan[] RetryDelays =
        [TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(30), TimeSpan.FromHours(2), TimeSpan.FromHours(6), TimeSpan.FromHours(12), TimeSpan.FromHours(24)];
    public static int MaxAttempts => RetryDelays.Length + 1;

    public static string Sign(string secret, long timestamp, string body)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        return $"t={timestamp},v1={Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes($"{timestamp}.{body}"))).ToLowerInvariant()}";
    }

    public static string? UrlProblem(string? url, bool livemode)
    {
        if (!Uri.TryCreate(url?.Trim(), UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http")) return "Enter a full URL, e.g. https://acme.in/hooks/paylink.";
        if (livemode && uri.Scheme != "https") return "Live webhooks must use https.";
        return null;
    }

    // ---------- endpoints ----------

    public async Task<IReadOnlyList<WebhookEndpoint>> EndpointsAsync(TenantRecord tenant)
    {
        await using var c = await tenantDb.OpenAsync(tenant);
        return (await c.QueryAsync<WebhookEndpoint>("SELECT * FROM webhook_endpoints ORDER BY id;")).ToList();
    }

    /// <summary>Adds an endpoint and returns its signing secret, shown once.</summary>
    public async Task<(WebhookEndpoint Endpoint, string Secret)> AddEndpointAsync(TenantRecord tenant, string url, bool livemode, IEnumerable<string>? events, string? description, Actor actor)
    {
        var secret = "whsec_" + WebSvc.PayLink.Storage.Provisioning.ProvisioningNames.RandomToken(32, includeUpper: true);
        var list = events?.Where(WebhookEvents.All.Contains).ToList() ?? [];
        var endpoint = new WebhookEndpoint
        {
            Url = url.Trim(), Livemode = livemode, SecretEnc = secrets.Protect(secret), Events = list.Count == 0 ? "*" : string.Join(",", list),
            Enabled = true, Description = description, CreatedAt = Clock.Now(), CreatedBy = actor.Name,
        };
        await using var c = await tenantDb.OpenAsync(tenant);
        await c.ExecuteAsync("INSERT INTO webhook_endpoints (url, livemode, secret_enc, events, enabled, description, created_at, created_by) " +
                             "VALUES (@Url, @Livemode, @SecretEnc, @Events, @Enabled, @Description, @CreatedAt, @CreatedBy);", endpoint);
        endpoint.Id = await c.ExecuteScalarAsync<long>(Db(tenant).LastInsertId);
        await AuditLog.WriteAsync(c, actor, "webhook.endpoint_added", endpoint.Url, $"{(livemode ? "live" : "test")} · {endpoint.Events}");
        return (endpoint, secret);
    }

    public async Task<bool> RemoveEndpointAsync(TenantRecord tenant, long id, Actor actor)
    {
        await using var c = await tenantDb.OpenAsync(tenant);
        var url = await c.ExecuteScalarAsync<string?>("SELECT url FROM webhook_endpoints WHERE id = @id;", new { id });
        if (url is null) return false;
        await c.ExecuteAsync("DELETE FROM webhook_endpoints WHERE id = @id;", new { id });
        await c.ExecuteAsync("UPDATE webhook_deliveries SET status = 'failed', next_attempt_at = NULL WHERE endpoint_id = @id AND status = 'pending';", new { id });
        await AuditLog.WriteAsync(c, actor, "webhook.endpoint_removed", url);
        return true;
    }

    // ---------- deliveries ----------

    /// <summary>Queues an event for every enabled endpoint of the same mode that wants it. Runs on the caller's connection.</summary>
    public async Task EnqueueAsync(DbConnection c, string eventType, bool livemode, string? paymentId, object data, long? onlyEndpointId = null)
    {
        var endpoints = (await c.QueryAsync<WebhookEndpoint>("SELECT * FROM webhook_endpoints WHERE enabled = TRUE AND livemode = @livemode;", new { livemode }))
            .Where(e => onlyEndpointId is null ? e.Wants(eventType) : e.Id == onlyEndpointId).ToList();
        if (endpoints.Count == 0) return;
        var eventId = Ids.New("evt");
        var now = Clock.Now();
        var payload = JsonSerializer.Serialize(new { id = eventId, type = eventType, created_at = now, livemode, data }, PaymentJson.Options);
        foreach (var e in endpoints)
            await c.ExecuteAsync(
                "INSERT INTO webhook_deliveries (endpoint_id, event_id, event_type, payment_id, livemode, payload, status, attempt_count, next_attempt_at, created_at) " +
                "VALUES (@endpoint, @eventId, @eventType, @paymentId, @livemode, @payload, 'pending', 0, @now, @now);",
                new { endpoint = e.Id, eventId, eventType, paymentId, livemode, payload, now });
    }

    public async Task SendTestAsync(TenantRecord tenant, long endpointId)
    {
        await using var c = await tenantDb.OpenAsync(tenant);
        var e = await c.QueryFirstOrDefaultAsync<WebhookEndpoint>("SELECT * FROM webhook_endpoints WHERE id = @endpointId;", new { endpointId });
        if (e is null) return;
        await EnqueueAsync(c, WebhookEvents.Test, e.Livemode, null, new { message = "A test event from PayLink. Your endpoint is reachable." }, e.Id);
    }

    public async Task<IReadOnlyList<WebhookDelivery>> DeliveriesAsync(TenantRecord tenant, string? paymentId = null, int take = 50)
    {
        await using var c = await tenantDb.OpenAsync(tenant);
        return (await c.QueryAsync<WebhookDelivery>(
            $"SELECT * FROM webhook_deliveries {(paymentId is null ? "" : "WHERE payment_id = @paymentId")} ORDER BY id DESC LIMIT {take};", new { paymentId })).ToList();
    }

    public async Task<bool> RedeliverAsync(TenantRecord tenant, long deliveryId, Actor actor)
    {
        await using var c = await tenantDb.OpenAsync(tenant);
        var n = await c.ExecuteAsync("UPDATE webhook_deliveries SET status = 'pending', next_attempt_at = @now WHERE id = @deliveryId;", new { deliveryId, now = Clock.Now() });
        if (n > 0) await AuditLog.WriteAsync(c, actor, "webhook.redelivered", deliveryId.ToString());
        return n > 0;
    }

    /// <summary>Sends every delivery that's due for one tenant. Called by the background job.</summary>
    public async Task<int> DeliverDueAsync(TenantRecord tenant, CancellationToken ct = default)
    {
        List<(WebhookDelivery Delivery, WebhookEndpoint? Endpoint)> due;
        await using (var c = await tenantDb.OpenAsync(tenant))
        {
            var rows = await c.QueryAsync<WebhookDelivery>(
                "SELECT * FROM webhook_deliveries WHERE status = 'pending' AND next_attempt_at <= @now ORDER BY id LIMIT 50;", new { now = Clock.Now() });
            var endpoints = (await c.QueryAsync<WebhookEndpoint>("SELECT * FROM webhook_endpoints;")).ToDictionary(e => e.Id);
            due = rows.Select(d => (d, endpoints.GetValueOrDefault(d.EndpointId))).ToList();
        }

        var http = httpFactory.CreateClient(HttpClientName);
        foreach (var (d, endpoint) in due)
        {
            if (ct.IsCancellationRequested) break;
            int? code = null;
            string response;
            var watch = Stopwatch.StartNew();
            if (endpoint is null) response = "The endpoint was removed.";
            else
            {
                try
                {
                    var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                    using var request = new HttpRequestMessage(HttpMethod.Post, endpoint.Url) { Content = new StringContent(d.Payload, Encoding.UTF8, "application/json") };
                    request.Headers.Add("PayLink-Signature", Sign(secrets.Unprotect(endpoint.SecretEnc), ts, d.Payload));
                    request.Headers.Add("PayLink-Event", d.EventType);
                    request.Headers.Add("PayLink-Delivery", d.Id.ToString());
                    request.Headers.UserAgent.ParseAdd("PayLink-Webhooks/1.0");
                    using var r = await http.SendAsync(request, ct);
                    code = (int)r.StatusCode;
                    response = await r.Content.ReadAsStringAsync(ct);
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or BlockedAddressException)
                {
                    response = ex is TaskCanceledException ? "Timed out after 10 seconds." : (ex.InnerException as BlockedAddressException ?? ex).Message;
                }
            }
            var ok = code is >= 200 and < 300;
            var attempts = d.AttemptCount + 1;
            var status = ok ? "delivered" : endpoint is null || attempts >= MaxAttempts ? "failed" : "pending";
            var next = status == "pending" ? Clock.Format(DateTime.UtcNow + RetryDelays[Math.Min(attempts - 1, RetryDelays.Length - 1)]) : null;
            await using var c = await tenantDb.OpenAsync(tenant);
            await c.ExecuteAsync(
                "UPDATE webhook_deliveries SET status = @status, attempt_count = @attempts, next_attempt_at = @next, last_attempt_at = @now, " +
                "last_status_code = @code, last_response = @response, last_duration_ms = @ms WHERE id = @id;",
                new { status, attempts, next, now = Clock.Now(), code, response = Trim(response, 500), ms = (int)watch.ElapsedMilliseconds, id = d.Id });
            if (!ok) logger.LogInformation("Webhook {Delivery} for {Tenant} failed ({Code}); {Status}", d.Id, tenant.Slug, code, status);
        }
        return due.Count;
    }

    private WebSvc.PayLink.Storage.SqlDialect Db(TenantRecord tenant) => WebSvc.PayLink.Storage.Db.Dialect(tenantDb.Target(tenant).Engine);

    private static string Trim(string value, int max) => value.Length <= max ? value : value[..max];

    /// <summary>
    /// The handler for webhook calls. It connects only to public addresses unless PayLink:Webhooks:AllowPrivateNetworks is true,
    /// so a tenant can't use webhooks to reach PayLink's own network. The check runs on the address actually connected to.
    /// </summary>
    public static SocketsHttpHandler CreateHandler(bool allowPrivateNetworks) => new()
    {
        AllowAutoRedirect = false,
        ConnectCallback = async (context, ct) =>
        {
            var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, ct);
            var allowed = addresses.Where(a => allowPrivateNetworks || !IsPrivate(a)).ToArray();
            if (allowed.Length == 0) throw new BlockedAddressException($"{context.DnsEndPoint.Host} is a private or local address; webhooks go to public addresses only.");
            var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(allowed, context.DnsEndPoint.Port, ct);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch { socket.Dispose(); throw; }
        },
    };

    public static bool IsPrivate(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any)) return true;
        if (address.AddressFamily == AddressFamily.InterNetworkV6)
            return address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6UniqueLocal;
        var b = address.GetAddressBytes();
        return b[0] == 10 || b[0] == 127 || b[0] == 0 || (b[0] == 169 && b[1] == 254) || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
               || (b[0] == 192 && b[1] == 168) || (b[0] == 100 && b[1] >= 64 && b[1] <= 127);
    }
}

public sealed class BlockedAddressException(string message) : Exception(message);
