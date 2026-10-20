using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dapper;
using PayLink.Infrastructure;
using PayLink.Platform;
using PayLink.Tenancy;
using WebSvc.PayLink.Abstractions;
using WebSvc.PayLink.Connector.Simulator;
using WebSvc.PayLink.Connector.Upi;

namespace PayLink.Payments;

public sealed record CreatePaymentRequest(
    long Amount, string? Currency, string? Reference, string? Description,
    string? CustomerName, string? CustomerEmail, string? CustomerPhone, string? CustomerId,
    string? ReturnUrl, Dictionary<string, string>? Metadata, int? ExpiresInSeconds);

/// <summary>An error the API turns into a JSON error and the checkout into a plain message.</summary>
public sealed record PaymentError(int Status, string Code, string Message);

public sealed record PaymentResult(Payment? Payment, PaymentError? Error, bool Replayed = false, Attempt? Attempt = null)
{
    public static PaymentResult Fail(int status, string code, string message) => new(null, new PaymentError(status, code, message));
    public static PaymentResult Fail(Payment payment, string code, string message) => new(payment, new PaymentError(400, code, message));
}

/// <summary>Who caused a change, for the payment's journey.</summary>
public sealed record EventSource(Actor Actor, string? UserAgent = null)
{
    public const string DeviceCookie = "paylink_device";
    /// <summary>The payer's browser, from a cookie the checkout sets; NewDevice when it had none yet (risk also treats a device with no earlier attempts as new).</summary>
    public string? DeviceId { get; init; }
    public bool NewDevice { get; init; }

    public static EventSource Payer(HttpContext http)
    {
        var device = http.Request.Cookies[DeviceCookie] ?? http.Items[DeviceCookie] as string;
        var isNew = string.IsNullOrEmpty(http.Request.Cookies[DeviceCookie]);
        if (string.IsNullOrEmpty(device))
        {
            device = Ids.New("dev", 16);
            http.Items[DeviceCookie] = device;   // once per request
            http.Response.Cookies.Append(DeviceCookie, device, new CookieOptions
            {
                HttpOnly = true, Secure = http.Request.IsHttps, SameSite = SameSiteMode.Lax, MaxAge = TimeSpan.FromDays(365), Path = "/pay",
            });
        }
        return new(new Actor("payer", null, "Payer", http.Connection.RemoteIpAddress?.ToString()), Trim(http.Request.Headers.UserAgent.ToString(), 500))
        { DeviceId = device, NewDevice = isNew };
    }
    public static readonly EventSource System = new(Actor.System);
    private static string? Trim(string? value, int max) => string.IsNullOrEmpty(value) ? null : value.Length <= max ? value : value[..max];
}

/// <summary>
/// The payment core. A payment is what the merchant asked for; an attempt is one try through one connector. Every change is
/// appended to the payment's event log (its journey), statuses only move forward, and merchants hear about changes through webhooks.
/// </summary>
public sealed partial class PaymentService(
    TenantDb tenantDb,
    PlatformDb platformDb,
    PlatformSettings platform,
    ConnectorRegistry registry,
    ConnectorSettingsService methods,
    WebhookService webhooks,
    IGeoLocator geo,
    ILogger<PaymentService> logger)
{
    public const int MaxAttempts = 5;
    public static readonly TimeSpan DefaultExpiry = TimeSpan.FromMinutes(30);

    // ---------- create ----------

    public static string? Validate(CreatePaymentRequest r)
    {
        if (r.Amount < 1 || r.Amount > 100_000_000_000) return "amount must be a whole number of minor units (paise for INR), from 1.";
        if (r.Currency is { } cur && (cur.Length != 3 || !cur.All(char.IsLetter))) return "currency must be a 3-letter code such as INR.";
        if (r.Reference?.Length > 100) return "reference can be at most 100 characters.";
        if (r.Description?.Length > 300) return "description can be at most 300 characters.";
        if (r.CustomerEmail is { Length: > 0 } email && (!email.Contains('@') || email.Length > 320)) return "customer.email isn't an email address.";
        if (r.CustomerPhone?.Length > 40 || r.CustomerName?.Length > 200 || r.CustomerId?.Length > 100) return "a customer field is too long.";
        if (r.ReturnUrl is { Length: > 0 } url && (!Uri.TryCreate(url, UriKind.Absolute, out var u) || u.Scheme is not ("https" or "http") || url.Length > 1000))
            return "return_url must be a full http(s) URL.";
        if (r.Metadata is { } m && (m.Count > 20 || m.Any(kv => kv.Key.Length > 40 || kv.Value?.Length > 500))) return "metadata allows up to 20 keys of 40 characters with values up to 500 characters.";
        if (r.ExpiresInSeconds is { } s && (s < 60 || s > 7 * 24 * 3600)) return "expires_in must be between 60 seconds and 7 days.";
        return null;
    }

    public async Task<PaymentResult> CreateAsync(TenantRecord tenant, CreatePaymentRequest r, bool livemode, string? idempotencyKey,
        EventSource source, string via, long? apiKeyId = null, string? linkId = null)
    {
        if (Validate(r) is { } problem) return PaymentResult.Fail(400, "invalid_request", problem);
        if (idempotencyKey?.Length > 200) return PaymentResult.Fail(400, "invalid_request", "Idempotency-Key can be at most 200 characters.");
        if (livemode && !await methods.IsLiveReadyAsync(tenant))
            return PaymentResult.Fail(400, "live_not_ready", "Live payments need at least one payment method with Live settings that passed their check. Use a test key until then.");

        var currency = (r.Currency ?? "INR").ToUpperInvariant();
        var (maxAmount, currencies) = await platform.GetLimitsAsync();
        if (maxAmount is { } max && r.Amount > max)
            return PaymentResult.Fail(400, "amount_too_large", $"This platform takes payments up to {Money.Format(max, currency)}.");
        if (currencies is not null && !currencies.Contains(currency))
            return PaymentResult.Fail(400, "currency_not_allowed", $"This platform takes payments in {string.Join(", ", currencies)}.");
        var hash = Hash(JsonSerializer.Serialize(r with { Currency = currency }));
        await using var c = await tenantDb.OpenAsync(tenant);

        if (!string.IsNullOrEmpty(idempotencyKey))
        {
            var existing = await c.QueryFirstOrDefaultAsync<Payment>(
                "SELECT * FROM payments WHERE livemode = @livemode AND idempotency_key = @idempotencyKey;", new { livemode, idempotencyKey });
            if (existing is not null)
                return existing.RequestHash == hash
                    ? new PaymentResult(existing, null, Replayed: true)
                    : PaymentResult.Fail(409, "idempotency_conflict", "This Idempotency-Key was already used with a different request.");
        }
        else if (!string.IsNullOrEmpty(r.Reference))
        {
            // The same order reference again returns the payment already started for it, instead of charging twice.
            var existing = await c.QueryFirstOrDefaultAsync<Payment>(
                "SELECT * FROM payments WHERE livemode = @livemode AND reference = @reference AND status NOT IN ('failed', 'cancelled', 'expired') ORDER BY created_at DESC LIMIT 1;",
                new { livemode, reference = r.Reference });
            if (existing is not null)
                return existing.Amount == r.Amount && existing.Currency == currency
                    ? new PaymentResult(existing, null, Replayed: true)
                    : PaymentResult.Fail(409, "reference_conflict", $"Payment {existing.Id} for reference {r.Reference} is open or paid with a different amount.");
        }

        var now = DateTime.UtcNow;
        var p = new Payment
        {
            Id = Ids.New("pay", 14), Livemode = livemode, Status = PaymentStatuses.Created, Amount = r.Amount, Currency = currency,
            Reference = Blank(r.Reference), Description = Blank(r.Description), CustomerName = Blank(r.CustomerName), CustomerEmail = Blank(r.CustomerEmail),
            CustomerPhone = Blank(r.CustomerPhone), CustomerId = Blank(r.CustomerId), ReturnUrl = Blank(r.ReturnUrl),
            Metadata = r.Metadata is { Count: > 0 } ? JsonSerializer.Serialize(r.Metadata) : null,
            IdempotencyKey = Blank(idempotencyKey), RequestHash = hash, CreatedVia = via, CreatedBy = source.Actor.Name, ApiKeyId = apiKeyId,
            LinkId = linkId, TraceId = Ids.New("trc", 12), CreatedAt = Clock.Format(now), UpdatedAt = Clock.Format(now),
            ExpiresAt = Clock.Format(now + (r.ExpiresInSeconds is { } s ? TimeSpan.FromSeconds(s) : DefaultExpiry)),
        };

        var decision = RiskEngine.OnCreate(await RulesAsync(c), p);
        await using (var pc = await platformDb.OpenAsync())
            await pc.ExecuteAsync("INSERT INTO payment_routes (payment_id, tenant_id, created_at) VALUES (@Id, @tenantId, @CreatedAt);",
                new { p.Id, tenantId = tenant.Id, p.CreatedAt });
        try
        {
            await c.ExecuteAsync(
                "INSERT INTO payments (id, livemode, status, amount, currency, reference, description, customer_name, customer_email, customer_phone, customer_id, " +
                "return_url, metadata, idempotency_key, request_hash, created_via, created_by, api_key_id, link_id, trace_id, created_at, updated_at, expires_at, attempt_count) " +
                "VALUES (@Id, @Livemode, @Status, @Amount, @Currency, @Reference, @Description, @CustomerName, @CustomerEmail, @CustomerPhone, @CustomerId, " +
                "@ReturnUrl, @Metadata, @IdempotencyKey, @RequestHash, @CreatedVia, @CreatedBy, @ApiKeyId, @LinkId, @TraceId, @CreatedAt, @UpdatedAt, @ExpiresAt, 0);", p);
        }
        catch (DbException) when (idempotencyKey is not null)
        {
            // Two requests with the same key at once: the other one won.
            var winner = await c.QueryFirstOrDefaultAsync<Payment>(
                "SELECT * FROM payments WHERE livemode = @livemode AND idempotency_key = @idempotencyKey;", new { livemode, idempotencyKey });
            if (winner is null) throw;
            return winner.RequestHash == hash ? new PaymentResult(winner, null, true) : PaymentResult.Fail(409, "idempotency_conflict", "This Idempotency-Key was already used with a different request.");
        }
        await AppendAsync(c, p, null, "payment.created", null, p.Status, source,
            new { amount = p.Amount, currency = p.Currency, reference = p.Reference, via, expires_at = p.ExpiresAt });

        if (decision.Blocked)
            await MoveAsync(c, p, PaymentStatuses.Failed, "risk.blocked", source, RuleData(decision), x =>
            {
                x.FailureReason = FailureReasons.RiskBlocked;
                x.FailureMessage = "Blocked by a risk rule.";
            });
        else if (decision.Review)
            await MoveAsync(c, p, PaymentStatuses.OnHold, "risk.review", source, RuleData(decision), x => x.HoldReason = $"Rule: {decision.Explanation}");
        return new PaymentResult(p, null);
    }

    // ---------- checkout ----------

    /// <summary>Starts an attempt with the chosen method and returns what the payer's browser should do next.</summary>
    public async Task<(PaymentResult Result, NextAction? Next)> StartAttemptAsync(TenantRecord tenant, string paymentId, string connectorId, EventSource source)
    {
        await using var c = await tenantDb.OpenAsync(tenant);
        var p = await GetAsync(c, paymentId);
        if (p is null) return (PaymentResult.Fail(404, "not_found", "Payment not found."), null);
        if (await ExpireIfDueAsync(c, p)) return (PaymentResult.Fail(p, "expired", "This payment has expired."), null);
        if (p.IsFinal) return (PaymentResult.Fail(p, "final", "This payment is already " + PaymentStatuses.Label(p.Status).ToLowerInvariant() + "."), null);
        if (p.Status == PaymentStatuses.OnHold) return (PaymentResult.Fail(p, "on_hold", "This payment is being reviewed. Please try again later."), null);
        if (p.Status == PaymentStatuses.Processing) return (PaymentResult.Fail(p, "processing", "This payment is already being processed."), null);

        var connector = (await methods.ForCheckoutAsync(tenant, p)).FirstOrDefault(x => x.Manifest.Id == connectorId);
        if (connector is null) return (PaymentResult.Fail(p, "method_unavailable", "That payment method isn't available for this payment."), null);

        if (p.AttemptCount >= MaxAttempts)
        {
            await MoveAsync(c, p, PaymentStatuses.Failed, "payment.too_many_attempts", source, new { attempts = p.AttemptCount }, x =>
            {
                x.FailureReason = FailureReasons.TooManyAttempts;
                x.FailureMessage = $"Stopped after {MaxAttempts} attempts.";
            });
            return (PaymentResult.Fail(p, "too_many_attempts", "Too many attempts. Ask the merchant for a new payment link."), null);
        }

        var country = geo.Country(source.Actor.Ip);
        // New device: this browser has never made an attempt with this tenant (the cookie alone is set on the first page view).
        var newDevice = source.NewDevice || (source.DeviceId is { } deviceId &&
            await c.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM attempts WHERE device_id = @deviceId;", new { deviceId }) == 0);
        var decision = await RiskEngine.OnAttemptAsync(c, await RulesAsync(c),
            new RiskEngine.AttemptContext(p, source.Actor.Ip, country, geo.IsTor(source.Actor.Ip), newDevice, p.Livemode));
        await AppendAsync(c, p, null, "risk.scored", p.Status, p.Status, source,
            new { score = decision.Score, signals = decision.Signals, country, decision = decision.Action });
        if (decision.Blocked)
        {
            await MoveAsync(c, p, PaymentStatuses.Failed, "risk.blocked", source, RuleData(decision), x =>
            {
                x.FailureReason = FailureReasons.RiskBlocked;
                x.FailureMessage = "Blocked by a risk rule.";
            });
            return (PaymentResult.Fail(p, "blocked", "This payment can't be completed. Please contact the merchant."), null);
        }
        if (decision.Review)
        {
            await MoveAsync(c, p, PaymentStatuses.OnHold, "risk.review", source, RuleData(decision), x => x.HoldReason = $"Rule: {decision.Explanation}");
            return (PaymentResult.Fail(p, "on_hold", "This payment needs a quick review by the merchant. Please try again later."), null);
        }

        // Switching method: the unfinished attempt is cancelled; it's still one payment.
        foreach (var open in (await AttemptsAsync(c, p.Id)).Where(a => a.Status == AttemptStatuses.RequiresAction))
            await MoveAttemptAsync(c, p, open, AttemptStatuses.Cancelled, "attempt.abandoned", source, null);

        var now = Clock.Now();
        var attempt = new Attempt
        {
            Id = Ids.New("att", 14), PaymentId = p.Id, Connector = connector.Manifest.Id, Method = ConnectorRegistry.MethodOf(connector.Manifest),
            Status = AttemptStatuses.RequiresAction, Ip = source.Actor.Ip, UserAgent = source.UserAgent, CreatedAt = now, UpdatedAt = now,
            IpCountry = country, DeviceId = source.DeviceId, RiskScore = decision.Score,
        };
        await c.ExecuteAsync(
            "INSERT INTO attempts (id, payment_id, connector, method, status, ip, user_agent, created_at, updated_at, ip_country, device_id, risk_score) " +
            "VALUES (@Id, @PaymentId, @Connector, @Method, @Status, @Ip, @UserAgent, @CreatedAt, @UpdatedAt, @IpCountry, @DeviceId, @RiskScore);", attempt);
        p.AttemptCount++;

        NextAction next;
        try
        {
            var config = await methods.ConfigAsync(tenant, connector.Manifest.Id, p.Mode);
            var request = new AttemptRequest(p.Id, attempt.Id, p.Amount, p.Currency, p.Description ?? p.Reference ?? "",
                new PayerDetails(p.CustomerName, p.CustomerEmail, p.CustomerPhone),
                $"{await platform.GetPublicBaseUrlAsync()}/pay/{p.Id}/return?attempt={attempt.Id}", PaymentJson.Metadata(p.Metadata) ?? new Dictionary<string, string>());
            next = await registry.CallAsync(connector, ct => connector.CreateAttemptAsync(request, config, ct));
        }
        catch (ConnectorCallException ex)
        {
            next = new NextAction.Failed("", FailureReason.GatewayError, ex.Code, ex.Message);
        }

        attempt.NextAction = NextActionJson.ToJson(next);
        attempt.GatewayRef = string.IsNullOrEmpty(next.GatewayReference) ? null : next.GatewayReference;
        await c.ExecuteAsync("UPDATE attempts SET next_action = @NextAction, gateway_ref = @GatewayRef WHERE id = @Id;", attempt);

        if (next is NextAction.Failed failed)
        {
            await FailAttemptAsync(c, p, attempt, FailureReasons.From(failed.Reason), failed.GatewayCode, failed.GatewayMessage, source);
            return (PaymentResult.Fail(p, "attempt_failed", $"{connector.Manifest.Name} couldn't start. Please try another method."), null);
        }

        await MoveAsync(c, p, PaymentStatuses.RequiresAction, "attempt.started", source,
            new { connector = attempt.Connector, method = attempt.Method, action = next.GetType().Name.ToLowerInvariant() },
            x =>
            {
                x.Connector = attempt.Connector;
                x.Method = attempt.Method;
                x.FailureReason = null;   // the earlier attempt's failure no longer describes the payment
                x.FailureMessage = null;
            }, attemptId: attempt.Id);
        return (new PaymentResult(p, null, Attempt: attempt), next);
    }

    /// <summary>PayLink UPI: the payer enters the UTR from their UPI app. The payment waits for the merchant to check it.</summary>
    public async Task<PaymentResult> SubmitUtrAsync(TenantRecord tenant, string paymentId, string attemptId, string? utr, string? proofFile, EventSource source)
    {
        utr = utr?.Replace(" ", "").Trim();
        if (!UpiConnector.IsValidUtr(utr)) return PaymentResult.Fail(400, "invalid_utr", "Enter the 12-digit UTR (UPI reference) from your UPI app.");
        await using var c = await tenantDb.OpenAsync(tenant);
        var p = await GetAsync(c, paymentId);
        var a = p is null ? null : (await AttemptsAsync(c, p.Id)).FirstOrDefault(x => x.Id == attemptId);
        if (p is null || a is null || a.Connector != UpiConnector.Id) return PaymentResult.Fail(404, "not_found", "Payment not found.");
        if (a.Status != AttemptStatuses.RequiresAction || p.IsFinal) return PaymentResult.Fail(p, "not_waiting", "This payment isn't waiting for a UPI reference.");

        var reused = await c.ExecuteScalarAsync<string?>(
            "SELECT payment_id FROM attempts WHERE utr = @utr AND payment_id <> @paymentId AND status IN ('processing', 'succeeded') LIMIT 1;", new { utr, paymentId });
        if (reused is not null)
        {
            await AppendAsync(c, p, a.Id, "upi.utr_reused", p.Status, p.Status, source, new { utr, other_payment = reused });
            return PaymentResult.Fail(p, "utr_used", "This UPI reference was already used for another payment. Check the number in your UPI app.");
        }

        a.Utr = utr;
        a.ProofFile = proofFile;
        await c.ExecuteAsync("UPDATE attempts SET utr = @Utr, proof_file = @ProofFile WHERE id = @Id;", a);
        await MoveAttemptAsync(c, p, a, AttemptStatuses.Processing, "upi.utr_submitted", source, new { utr, proof = proofFile is not null });
        await MoveAsync(c, p, PaymentStatuses.Processing, "payment.processing", source, null, x => x.Utr = utr, attemptId: a.Id);
        return new PaymentResult(p, null, Attempt: a);
    }

    /// <summary>The merchant confirms (or rejects) a UPI payment after checking the UTR in their bank or UPI app. A note is required.</summary>
    public async Task<PaymentResult> VerifyUpiAsync(TenantRecord tenant, string attemptId, bool approve, string? note, EventSource source)
    {
        if (string.IsNullOrWhiteSpace(note)) return PaymentResult.Fail(400, "note_required", "Add a note, e.g. where you checked the UTR.");
        await using var c = await tenantDb.OpenAsync(tenant);
        var a = await c.QueryFirstOrDefaultAsync<Attempt>("SELECT * FROM attempts WHERE id = @attemptId;", new { attemptId });
        var p = a is null ? null : await GetAsync(c, a.PaymentId);
        if (a is null || p is null || a.Connector != UpiConnector.Id) return PaymentResult.Fail(404, "not_found", "UPI payment not found.");
        if (a.Status != AttemptStatuses.Processing) return PaymentResult.Fail(p, "not_waiting", "This UPI payment has already been decided.");

        var data = new { utr = a.Utr, note = note.Trim() };
        if (approve)
        {
            await MoveAttemptAsync(c, p, a, AttemptStatuses.Succeeded, "upi.approved", source, data);
            await SucceedAsync(c, p, a, source);
        }
        else
        {
            await MoveAttemptAsync(c, p, a, AttemptStatuses.Failed, "upi.rejected", source, data, x =>
            {
                x.FailureReason = FailureReasons.Rejected;
                x.GatewayMessage = note.Trim();
            });
            await MoveAsync(c, p, PaymentStatuses.Failed, "payment.failed", source, null, x =>
            {
                x.FailureReason = FailureReasons.Rejected;
                x.FailureMessage = "The merchant couldn't find this UPI payment: " + note.Trim();
            }, attemptId: a.Id);
        }
        await AuditLog.WriteAsync(c, source.Actor, approve ? "upi.approved" : "upi.rejected", p.Id, $"UTR {a.Utr}: {note.Trim()}");
        return new PaymentResult(p, null, Attempt: a);
    }

    /// <summary>Test mode: the payer picked Succeed, Fail or Stay pending on the Simulator.</summary>
    public async Task<PaymentResult> SimulateAsync(TenantRecord tenant, string paymentId, string attemptId, string outcome, EventSource source)
    {
        if (!SimulatorConnector.Outcomes.Contains(outcome)) return PaymentResult.Fail(400, "invalid_request", "Unknown outcome.");
        await using var c = await tenantDb.OpenAsync(tenant);
        var p = await GetAsync(c, paymentId);
        var a = p is null ? null : (await AttemptsAsync(c, p.Id)).FirstOrDefault(x => x.Id == attemptId);
        if (p is null || a is null || a.Connector != SimulatorConnector.Id || p.Livemode) return PaymentResult.Fail(404, "not_found", "Payment not found.");
        if (AttemptStatuses.IsFinal(a.Status) || p.IsFinal) return PaymentResult.Fail(p, "final", "This attempt is already finished.");
        await ApplyAsync(c, p, a, SimulatorConnector.Outcome(outcome), source, "simulator.outcome");
        return new PaymentResult(p, null, Attempt: a);
    }

    /// <summary>Applies a status reported by a gateway (webhook or status check) to an attempt and its payment.</summary>
    public async Task ApplyAsync(DbConnection c, Payment p, Attempt a, AttemptStatusResult result, EventSource source, string eventType)
    {
        var data = new { status = result.Status.ToString().ToLowerInvariant(), code = result.GatewayCode, message = result.GatewayMessage, gateway_payment = result.ProviderPaymentId };
        if ((result.ProviderPaymentId is { Length: > 0 } && result.ProviderPaymentId != a.ProviderPaymentId) || (result.Method is { Length: > 0 } && result.Method != a.PaidMethod))
        {
            a.ProviderPaymentId = result.ProviderPaymentId ?? a.ProviderPaymentId;
            a.PaidMethod = result.Method ?? a.PaidMethod;
            await c.ExecuteAsync("UPDATE attempts SET provider_payment_id = @ProviderPaymentId, paid_method = @PaidMethod WHERE id = @Id;", a);
        }
        switch (result.Status)
        {
            case AttemptStatus.Succeeded:
                if (await MoveAttemptAsync(c, p, a, AttemptStatuses.Succeeded, eventType, source, data, x => x.GatewayCode = result.GatewayCode))
                    await SucceedAsync(c, p, a, source);
                break;
            case AttemptStatus.Processing:
                if (await MoveAttemptAsync(c, p, a, AttemptStatuses.Processing, eventType, source, data))
                    await MoveAsync(c, p, PaymentStatuses.Processing, "payment.processing", source, null, attemptId: a.Id);
                break;
            case AttemptStatus.RequiresAction:
                await AppendAsync(c, p, a.Id, eventType, p.Status, p.Status, source, data);
                break;
            default:
                if (AttemptStatuses.IsFinal(a.Status)) { await AppendAsync(c, p, a.Id, "attempt.status_ignored", p.Status, p.Status, source, data); break; }
                await AppendAsync(c, p, a.Id, eventType, p.Status, p.Status, source, data);
                await FailAttemptAsync(c, p, a, result.Status == AttemptStatus.Expired ? "expired" : FailureReasons.From(result.Reason),
                    result.GatewayCode, result.GatewayMessage, source, attemptStatus: result.Status switch
                    {
                        AttemptStatus.Cancelled => AttemptStatuses.Cancelled,
                        AttemptStatus.Expired => AttemptStatuses.Expired,
                        _ => null,
                    });
                break;
        }
    }

    // ---------- merchant actions ----------

    public async Task<PaymentResult> CancelAsync(TenantRecord tenant, string paymentId, string? note, EventSource source)
    {
        await using var c = await tenantDb.OpenAsync(tenant);
        var p = await GetAsync(c, paymentId);
        if (p is null) return PaymentResult.Fail(404, "not_found", "Payment not found.");
        if (p.IsFinal) return PaymentResult.Fail(p, "final", $"This payment is already {PaymentStatuses.Label(p.Status).ToLowerInvariant()}.");
        if (p.Status == PaymentStatuses.Processing)
            return PaymentResult.Fail(p, "processing", "The payer may already have paid. Approve or reject the UPI payment, or wait for the gateway.");
        foreach (var open in (await AttemptsAsync(c, p.Id)).Where(a => !AttemptStatuses.IsFinal(a.Status)))
            await MoveAttemptAsync(c, p, open, AttemptStatuses.Cancelled, "attempt.cancelled", source, null);
        await MoveAsync(c, p, PaymentStatuses.Cancelled, "payment.cancelled", source, new { note = Blank(note) }, x =>
        {
            x.FailureReason = "cancelled";
            x.FailureMessage = Blank(note);
        });
        return new PaymentResult(p, null);
    }

    public async Task<PaymentResult> ReleaseHoldAsync(TenantRecord tenant, string paymentId, string? note, EventSource source)
    {
        if (string.IsNullOrWhiteSpace(note)) return PaymentResult.Fail(400, "note_required", "Add a note saying why it's safe to continue.");
        await using var c = await tenantDb.OpenAsync(tenant);
        var p = await GetAsync(c, paymentId);
        if (p is null) return PaymentResult.Fail(404, "not_found", "Payment not found.");
        if (p.Status != PaymentStatuses.OnHold) return PaymentResult.Fail(p, "not_on_hold", "This payment isn't on hold.");
        await MoveAsync(c, p, PaymentStatuses.Created, "risk.released", source, new { note = note.Trim() }, x => x.HoldReason = null);
        await AuditLog.WriteAsync(c, source.Actor, "payment.released", p.Id, note.Trim());
        return new PaymentResult(p, null);
    }

    // ---------- background ----------

    /// <summary>Expires unpaid payments past their expiry time. Payments already processing are left alone.</summary>
    public async Task<int> ExpireDueAsync(TenantRecord tenant)
    {
        await using var c = await tenantDb.OpenAsync(tenant);
        var due = await c.QueryAsync<Payment>(
            "SELECT * FROM payments WHERE status IN ('created', 'on_hold', 'requires_action') AND expires_at < @now LIMIT 200;", new { now = Clock.Now() });
        var n = 0;
        foreach (var p in due) if (await ExpireIfDueAsync(c, p)) n++;
        return n;
    }

    private async Task<bool> ExpireIfDueAsync(DbConnection c, Payment p)
    {
        if (p.IsFinal || p.Status == PaymentStatuses.Processing || Clock.Parse(p.ExpiresAt) > DateTime.UtcNow) return false;
        foreach (var open in (await AttemptsAsync(c, p.Id)).Where(a => !AttemptStatuses.IsFinal(a.Status)))
            await MoveAttemptAsync(c, p, open, AttemptStatuses.Expired, "attempt.expired", EventSource.System, null);
        return await MoveAsync(c, p, PaymentStatuses.Expired, "payment.expired", EventSource.System, null, x =>
        {
            x.FailureReason = "expired";
            x.FailureMessage = "Not paid before it expired.";
        });
    }

    /// <summary>Records that the payer opened the checkout: once per payment and IP, so reloads don't flood the journey.</summary>
    public async Task RecordOpenedAsync(TenantRecord tenant, Payment p, EventSource source)
    {
        await using var c = await tenantDb.OpenAsync(tenant);
        var seen = await c.ExecuteScalarAsync<long>(
            "SELECT COUNT(*) FROM payment_events WHERE payment_id = @id AND type = 'checkout.opened' AND (ip = @ip OR (ip IS NULL AND @ip IS NULL));",
            new { id = p.Id, ip = source.Actor.Ip });
        if (seen == 0) await AppendAsync(c, p, null, "checkout.opened", p.Status, p.Status, source, null);
    }

    /// <summary>Re-reads a payment after expiring it if it's past its time.</summary>
    public async Task<Payment?> FreshAsync(TenantRecord tenant, string id)
    {
        await using var c = await tenantDb.OpenAsync(tenant);
        var p = await GetAsync(c, id);
        if (p is not null) await ExpireIfDueAsync(c, p);
        return p;
    }

    // ---------- reads ----------

    public async Task<Payment?> GetAsync(TenantRecord tenant, string id)
    {
        await using var c = await tenantDb.OpenAsync(tenant);
        return await GetAsync(c, id);
    }

    public async Task<(Payment Payment, IReadOnlyList<Attempt> Attempts, IReadOnlyList<PaymentEvent> Events)?> DetailAsync(TenantRecord tenant, string id)
    {
        await using var c = await tenantDb.OpenAsync(tenant);
        var p = await GetAsync(c, id);
        if (p is null) return null;
        var events = (await c.QueryAsync<PaymentEvent>("SELECT * FROM payment_events WHERE payment_id = @id ORDER BY id;", new { id })).ToList();
        return (p, await AttemptsAsync(c, id), events);
    }

    public async Task<IReadOnlyList<Attempt>> AttemptsAsync(TenantRecord tenant, string paymentId)
    {
        await using var c = await tenantDb.OpenAsync(tenant);
        return await AttemptsAsync(c, paymentId);
    }

    public async Task<IReadOnlyList<Payment>> SearchAsync(TenantRecord tenant, bool livemode, string? status, string? q, int take = 50, int skip = 0, string? reference = null, string? linkId = null)
    {
        var where = new List<string> { "livemode = @livemode" };
        if (!string.IsNullOrEmpty(reference)) where.Add("reference = @reference");
        if (!string.IsNullOrEmpty(linkId)) where.Add("link_id = @linkId");
        if (!string.IsNullOrEmpty(status) && PaymentStatuses.All.Contains(status)) where.Add("status = @status");
        if (!string.IsNullOrWhiteSpace(q))
            where.Add("(id = @q OR reference = @q OR utr = @q OR customer_email = @q OR customer_phone = @q OR customer_id = @q " +
                      "OR customer_name LIKE @like OR description LIKE @like OR id IN (SELECT payment_id FROM attempts WHERE utr = @q OR gateway_ref = @q OR id = @q))");
        await using var c = await tenantDb.OpenAsync(tenant);
        return (await c.QueryAsync<Payment>(
            $"SELECT * FROM payments WHERE {string.Join(" AND ", where)} ORDER BY created_at DESC LIMIT {Math.Clamp(take, 1, 200)} OFFSET {Math.Max(0, skip)};",
            new { livemode, status, q = q?.Trim(), like = $"%{q?.Trim()}%", reference, linkId })).ToList();
    }

    /// <summary>UPI payments whose payer entered a UTR, waiting for the merchant to check them.</summary>
    public async Task<IReadOnlyList<(Payment Payment, Attempt Attempt)>> UpiQueueAsync(TenantRecord tenant, bool livemode)
    {
        await using var c = await tenantDb.OpenAsync(tenant);
        var attempts = (await c.QueryAsync<Attempt>(
            "SELECT a.* FROM attempts a JOIN payments p ON p.id = a.payment_id WHERE a.connector = 'upi' AND a.status = 'processing' AND p.livemode = @livemode ORDER BY a.updated_at;",
            new { livemode })).ToList();
        var result = new List<(Payment, Attempt)>();
        foreach (var a in attempts)
            if (await GetAsync(c, a.PaymentId) is { } p) result.Add((p, a));
        return result;
    }

    public async Task<int> UpiQueueCountAsync(TenantRecord tenant)
    {
        await using var c = await tenantDb.OpenAsync(tenant);
        return await c.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM attempts WHERE connector = 'upi' AND status = 'processing';");
    }

    public sealed record Summary(long SucceededCount, long SucceededAmount, long OpenCount, long FailedCount, long OnHoldCount);

    public async Task<Summary> SummaryAsync(TenantRecord tenant, bool livemode, DateTime sinceUtc)
    {
        await using var c = await tenantDb.OpenAsync(tenant);
        var rows = (await c.QueryAsync<(string Status, long Count, long Amount)>(
            "SELECT status, COUNT(*), COALESCE(SUM(amount), 0) FROM payments WHERE livemode = @livemode AND created_at >= @since GROUP BY status;",
            new { livemode, since = Clock.Format(sinceUtc) })).ToList();
        long Count(params string[] s) => rows.Where(r => s.Contains(r.Status)).Sum(r => r.Count);
        return new Summary(Count(PaymentStatuses.Succeeded), rows.Where(r => r.Status == PaymentStatuses.Succeeded).Sum(r => r.Amount),
            Count(PaymentStatuses.Created, PaymentStatuses.RequiresAction, PaymentStatuses.Processing),
            Count(PaymentStatuses.Failed, PaymentStatuses.Expired, PaymentStatuses.Cancelled), Count(PaymentStatuses.OnHold));
    }

    // ---------- internals ----------

    private static async Task<Payment?> GetAsync(DbConnection c, string id) =>
        await c.QueryFirstOrDefaultAsync<Payment>("SELECT * FROM payments WHERE id = @id;", new { id });

    private static async Task<IReadOnlyList<Attempt>> AttemptsAsync(DbConnection c, string paymentId) =>
        (await c.QueryAsync<Attempt>("SELECT * FROM attempts WHERE payment_id = @paymentId ORDER BY created_at, id;", new { paymentId })).ToList();

    private static async Task<IReadOnlyList<RiskRule>> RulesAsync(DbConnection c) =>
        (await c.QueryAsync<RiskRule>("SELECT * FROM risk_rules;")).ToList();

    private async Task SucceedAsync(DbConnection c, Payment p, Attempt a, EventSource source) =>
        await MoveAsync(c, p, PaymentStatuses.Succeeded, "payment.succeeded", source, new { connector = a.Connector, method = a.Method }, x =>
        {
            x.PaidAt = Clock.Now();
            x.Connector = a.Connector;
            x.Method = a.PaidMethod ?? a.Method;
            x.SucceededAttemptId = a.Id;
            x.Utr = a.Utr ?? x.Utr;
            x.FailureReason = null;
            x.FailureMessage = null;
        }, attemptId: a.Id);

    /// <summary>An attempt failed: the payer may try another method, until the attempt limit.</summary>
    private async Task FailAttemptAsync(DbConnection c, Payment p, Attempt a, string reason, string? code, string? message, EventSource source,
        string eventType = "attempt.failed", string? attemptStatus = null)
    {
        await MoveAttemptAsync(c, p, a, attemptStatus ?? AttemptStatuses.Failed, eventType, source, new { reason, code, message }, x =>
        {
            x.FailureReason = reason;
            x.GatewayCode = code;
            x.GatewayMessage = message is null ? null : message.Length <= 500 ? message : message[..500];
        });
        var last = p.AttemptCount >= MaxAttempts;
        await MoveAsync(c, p, last ? PaymentStatuses.Failed : PaymentStatuses.RequiresAction, last ? "payment.failed" : "payment.retry_allowed", source, null, x =>
        {
            x.FailureReason = last ? FailureReasons.TooManyAttempts : reason;
            x.FailureMessage = message;
        }, retry: true, attemptId: a.Id);
    }

    /// <summary>Moves a payment forward if the rules allow it, records the event and queues the webhook. False when it couldn't move.</summary>
    private async Task<bool> MoveAsync(DbConnection c, Payment p, string to, string eventType, EventSource source, object? data,
        Action<Payment>? apply = null, bool retry = false, string? attemptId = null)
    {
        var from = p.Status;
        if (!PaymentStatuses.CanMove(from, to, retry))
        {
            await AppendAsync(c, p, attemptId, "status.ignored", from, from, source, new { requested = to, because = eventType });
            return false;
        }
        apply?.Invoke(p);
        p.Status = to;
        p.UpdatedAt = Clock.Now();
        var n = await c.ExecuteAsync(
            "UPDATE payments SET status = @Status, updated_at = @UpdatedAt, paid_at = @PaidAt, attempt_count = @AttemptCount, connector = @Connector, method = @Method, " +
            "succeeded_attempt_id = @SucceededAttemptId, utr = @Utr, failure_reason = @FailureReason, failure_message = @FailureMessage, hold_reason = @HoldReason " +
            "WHERE id = @Id AND status = @from;",
            new { p.Status, p.UpdatedAt, p.PaidAt, p.AttemptCount, p.Connector, p.Method, p.SucceededAttemptId, p.Utr, p.FailureReason, p.FailureMessage, p.HoldReason, p.Id, from });
        if (n == 0)
        {
            // Someone else moved it first; keep their result.
            var fresh = (await GetAsync(c, p.Id))!;
            logger.LogInformation("Payment {Payment} changed concurrently: wanted {From} -> {To}, found {Now}", p.Id, from, to, fresh.Status);
            Copy(fresh, p);
            return false;
        }
        await AppendAsync(c, p, attemptId, eventType, from, to, source, data);
        if (from != to && WebhookEvents.ForStatus(to) is { } webhook)
            await webhooks.EnqueueAsync(c, webhook, p.Livemode, p.Id, PaymentJson.From(p, await platform.GetPublicBaseUrlAsync(), await AttemptsAsync(c, p.Id)));
        return true;
    }

    private static async Task<bool> MoveAttemptAsync(DbConnection c, Payment p, Attempt a, string to, string eventType, EventSource source, object? data,
        Action<Attempt>? apply = null)
    {
        var from = a.Status;
        if (!AttemptStatuses.CanMove(from, to))
        {
            await AppendAsync(c, p, a.Id, "attempt.status_ignored", p.Status, p.Status, source, new { attempt_status = from, requested = to, because = eventType });
            return false;
        }
        apply?.Invoke(a);
        a.Status = to;
        a.UpdatedAt = Clock.Now();
        var n = await c.ExecuteAsync(
            "UPDATE attempts SET status = @Status, updated_at = @UpdatedAt, failure_reason = @FailureReason, gateway_code = @GatewayCode, gateway_message = @GatewayMessage " +
            "WHERE id = @Id AND status = @from;",
            new { a.Status, a.UpdatedAt, a.FailureReason, a.GatewayCode, a.GatewayMessage, a.Id, from });
        if (n == 0) return false;
        await AppendAsync(c, p, a.Id, eventType, p.Status, p.Status, source, data ?? new { attempt_status = to });
        return true;
    }

    private static async Task AppendAsync(DbConnection c, Payment p, string? attemptId, string type, string? before, string? after, EventSource source, object? data) =>
        await c.ExecuteAsync(
            "INSERT INTO payment_events (payment_id, attempt_id, at, type, status_before, status_after, actor_type, actor_id, actor_name, ip, user_agent, data) " +
            "VALUES (@paymentId, @attemptId, @at, @type, @before, @after, @actorType, @actorId, @actorName, @ip, @userAgent, @data);",
            new
            {
                paymentId = p.Id, attemptId, at = Clock.Now(), type, before, after, actorType = source.Actor.Type, actorId = source.Actor.Id,
                actorName = source.Actor.Name, ip = source.Actor.Ip, userAgent = source.UserAgent,
                data = data is null ? null : JsonSerializer.Serialize(data, PaymentJson.Options),
            });

    private static object RuleData(RiskDecision d) => new { rule_id = d.Rule?.Id, kind = d.Rule?.Kind, value = d.Rule?.Value, action = d.Action, note = d.Rule?.Note };

    private static void Copy(Payment from, Payment to)
    {
        foreach (var prop in typeof(Payment).GetProperties().Where(x => x.CanWrite)) prop.SetValue(to, prop.GetValue(from));
    }

    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
