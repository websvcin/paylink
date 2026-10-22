using System.Text.RegularExpressions;
using Dapper;
using PayLink.Infrastructure;
using PayLink.Platform;
using PayLink.Tenancy;

namespace PayLink.Payments;

public sealed class PaymentLink
{
    public string Id { get; set; } = "";
    public string Code { get; set; } = "";
    public bool Livemode { get; set; }
    public string Title { get; set; } = "";
    public string? Description { get; set; }
    /// <summary>A fixed amount; null when the payer chooses it, between MinAmount and MaxAmount.</summary>
    public long? Amount { get; set; }
    public long? MinAmount { get; set; }
    public long? MaxAmount { get; set; }
    public string Currency { get; set; } = "INR";
    public bool AskEmail { get; set; }
    public bool AskPhone { get; set; }
    public string? ReturnUrl { get; set; }
    /// <summary>Stops taking payments after this many succeeded ones (a one-time link is 1).</summary>
    public int? MaxPayments { get; set; }
    public string? ExpiresAt { get; set; }
    public bool Active { get; set; } = true;
    public string CreatedAt { get; set; } = "";
    public string? CreatedBy { get; set; }
    public string UpdatedAt { get; set; } = "";

    // Filled in by the list query.
    public long Paid { get; set; }
    public long Collected { get; set; }
}

public sealed record CreateLinkRequest(
    string Title, string? Description, long? Amount, long? MinAmount, long? MaxAmount, string? Currency,
    bool AskEmail, bool AskPhone, string? ReturnUrl, int? MaxPayments, string? ExpiresAt, string? Code);

/// <summary>
/// Payment links: a reusable page at /l/{code} that a merchant shares (a message, a QR on the counter, a button on a site).
/// Each visit that presses Pay creates an ordinary payment, so checkout, webhooks and reports all work the same.
/// Codes are unique across the platform (link_routes), the way payment ids are.
/// </summary>
public sealed partial class PaymentLinkService(TenantDb tenantDb, PlatformDb platformDb, PlatformRepository repo, PaymentService payments)
{
    public const int MaxCodeLength = 60;

    [GeneratedRegex("^[a-z0-9](?:[a-z0-9-]{1,58}[a-z0-9])?$")]
    private static partial Regex CodePattern();

    private static readonly HashSet<string> Reserved = ["new", "admin", "api", "pay", "console", "static", "js", "css"];

    public static string? Validate(CreateLinkRequest r)
    {
        if (string.IsNullOrWhiteSpace(r.Title) || r.Title.Length > 200) return "title is required, up to 200 characters.";
        if (r.Description?.Length > 1000) return "description can be at most 1000 characters.";
        if (r.Amount is { } a && (a < 1 || a > 100_000_000_000)) return "amount must be a whole number of minor units (paise for INR), from 1.";
        if (r.Amount is not null && (r.MinAmount is not null || r.MaxAmount is not null)) return "Give either a fixed amount, or min_amount/max_amount for the payer to choose.";
        if (r.MinAmount is < 1 || r.MaxAmount is < 1) return "min_amount and max_amount must be at least 1.";
        if (r.MinAmount is { } min && r.MaxAmount is { } max && min > max) return "min_amount is more than max_amount.";
        if (r.Currency is { } cur && (cur.Length != 3 || !cur.All(char.IsLetter))) return "currency must be a 3-letter code such as INR.";
        if (r.ReturnUrl is { Length: > 0 } url && (!Uri.TryCreate(url, UriKind.Absolute, out var u) || u.Scheme is not ("https" or "http") || url.Length > 1000))
            return "return_url must be a full http(s) URL.";
        if (r.MaxPayments is < 1) return "max_payments must be at least 1.";
        if (r.ExpiresAt is { Length: > 0 } e && !DateTime.TryParse(e, null, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out _))
            return "expires_at must be a date and time, e.g. 2026-12-31T18:30:00Z.";
        if (r.Code is { Length: > 0 } code && (!CodePattern().IsMatch(code) || Reserved.Contains(code)))
            return "code can use lowercase letters, digits and dashes (3 to 60), e.g. term-fees-2026.";
        return null;
    }

    public async Task<(PaymentLink? Link, string? Error)> CreateAsync(TenantRecord tenant, CreateLinkRequest r, bool livemode, Actor actor)
    {
        if (Validate(r) is { } problem) return (null, problem);
        var now = Clock.Now();
        var link = new PaymentLink
        {
            Id = Ids.New("lnk", 14),
            Code = string.IsNullOrEmpty(r.Code) ? Ids.New("x", 10)[2..].ToLowerInvariant() : r.Code,
            Livemode = livemode, Title = r.Title.Trim(), Description = Blank(r.Description), Amount = r.Amount, MinAmount = r.MinAmount, MaxAmount = r.MaxAmount,
            Currency = (r.Currency ?? "INR").ToUpperInvariant(), AskEmail = r.AskEmail, AskPhone = r.AskPhone, ReturnUrl = Blank(r.ReturnUrl),
            MaxPayments = r.MaxPayments,
            ExpiresAt = r.ExpiresAt is { Length: > 0 } e ? Clock.Format(DateTime.Parse(e, null, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal)) : null,
            CreatedAt = now, CreatedBy = actor.Name, UpdatedAt = now,
        };
        await using (var pc = await platformDb.OpenAsync())
        {
            // The code is claimed platform-wide first, so two tenants can't both take "fees".
            if (await pc.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM link_routes WHERE code = @Code;", link) > 0)
                return (null, $"The code {link.Code} is taken. Choose another.");
            try { await pc.ExecuteAsync("INSERT INTO link_routes (code, tenant_id, created_at) VALUES (@Code, @tenantId, @CreatedAt);", new { link.Code, tenantId = tenant.Id, link.CreatedAt }); }
            catch (System.Data.Common.DbException) { return (null, $"The code {link.Code} is taken. Choose another."); }
        }
        await using var c = await tenantDb.OpenAsync(tenant);
        await c.ExecuteAsync(
            "INSERT INTO payment_links (id, code, livemode, title, description, amount, min_amount, max_amount, currency, ask_email, ask_phone, return_url, " +
            "max_payments, expires_at, active, created_at, created_by, updated_at) VALUES (@Id, @Code, @Livemode, @Title, @Description, @Amount, @MinAmount, @MaxAmount, " +
            "@Currency, @AskEmail, @AskPhone, @ReturnUrl, @MaxPayments, @ExpiresAt, TRUE, @CreatedAt, @CreatedBy, @UpdatedAt);", link);
        await tenantDb.AuditAsync(tenant, actor, "link.created", link.Id, $"{link.Code} · {link.Title}");
        return (link, null);
    }

    public Task<IReadOnlyList<PaymentLink>> ListAsync(TenantRecord tenant, bool livemode) => ListAsync(tenant, livemode, null, null, 200, 0);

    /// <summary>Links, newest first, optionally matching a word (title, code, description) and a state ("on" or "off").</summary>
    public async Task<IReadOnlyList<PaymentLink>> ListAsync(TenantRecord tenant, bool livemode, string? q, string? state, int take, int skip)
    {
        var where = "l.livemode = @livemode" + (string.IsNullOrWhiteSpace(q) ? "" : " AND (LOWER(l.title) LIKE @like OR l.code LIKE @like OR LOWER(l.description) LIKE @like)")
            + (state == "on" ? " AND l.active = TRUE" : state == "off" ? " AND l.active = FALSE" : "");
        await using var c = await tenantDb.OpenAsync(tenant);
        return (await c.QueryAsync<PaymentLink>(
            "SELECT l.*, (SELECT COUNT(*) FROM payments p WHERE p.link_id = l.id AND p.status = 'succeeded') AS paid, " +
            "(SELECT COALESCE(SUM(p.amount), 0) FROM payments p WHERE p.link_id = l.id AND p.status = 'succeeded') AS collected " +
            $"FROM payment_links l WHERE {where} ORDER BY l.created_at DESC LIMIT {Math.Clamp(take, 1, 500)} OFFSET {Math.Max(0, skip)};",
            new { livemode, like = $"%{q?.Trim().ToLowerInvariant()}%" })).ToList();
    }

    public async Task<long> CountAsync(TenantRecord tenant, bool livemode, string? q, string? state)
    {
        var where = "livemode = @livemode" + (string.IsNullOrWhiteSpace(q) ? "" : " AND (LOWER(title) LIKE @like OR code LIKE @like OR LOWER(description) LIKE @like)")
            + (state == "on" ? " AND active = TRUE" : state == "off" ? " AND active = FALSE" : "");
        await using var c = await tenantDb.OpenAsync(tenant);
        return await c.ExecuteScalarAsync<long>($"SELECT COUNT(*) FROM payment_links WHERE {where};", new { livemode, like = $"%{q?.Trim().ToLowerInvariant()}%" });
    }

    public async Task<PaymentLink?> GetAsync(TenantRecord tenant, string id)
    {
        await using var c = await tenantDb.OpenAsync(tenant);
        return await c.QueryFirstOrDefaultAsync<PaymentLink>(
            "SELECT l.*, (SELECT COUNT(*) FROM payments p WHERE p.link_id = l.id AND p.status = 'succeeded') AS paid, " +
            "(SELECT COALESCE(SUM(p.amount), 0) FROM payments p WHERE p.link_id = l.id AND p.status = 'succeeded') AS collected " +
            "FROM payment_links l WHERE l.id = @id OR l.code = @id;", new { id });
    }

    /// <summary>Finds a link and its tenant from the public code.</summary>
    public async Task<(TenantRecord Tenant, PaymentLink Link)?> ByCodeAsync(string code)
    {
        if (code.Length > MaxCodeLength) return null;
        long? tenantId;
        await using (var pc = await platformDb.OpenAsync())
            tenantId = await pc.ExecuteScalarAsync<long?>("SELECT tenant_id FROM link_routes WHERE code = @code;", new { code = code.ToLowerInvariant() });
        if (tenantId is null || await repo.TenantByIdAsync(tenantId.Value) is not { } tenant) return null;
        return await GetAsync(tenant, code.ToLowerInvariant()) is { } link ? (tenant, link) : null;
    }

    public async Task<bool> SetActiveAsync(TenantRecord tenant, string id, bool active, Actor actor)
    {
        await using var c = await tenantDb.OpenAsync(tenant);
        var n = await c.ExecuteAsync("UPDATE payment_links SET active = @active, updated_at = @now WHERE id = @id;", new { id, active, now = Clock.Now() });
        if (n > 0) await tenantDb.AuditAsync(tenant, actor, active ? "link.activated" : "link.deactivated", id);
        return n > 0;
    }

    /// <summary>Why the link can't take a payment now, in words for the payer; null when it can.</summary>
    public static string? Closed(TenantRecord tenant, PaymentLink link)
    {
        if (!tenant.IsActive) return $"{tenant.Name} can't take payments right now.";
        if (!link.Active) return "This link is no longer taking payments.";
        if (link.ExpiresAt is not null && string.CompareOrdinal(link.ExpiresAt, Clock.Now()) <= 0) return "This link has expired.";
        if (link.MaxPayments is { } max && link.Paid >= max) return max == 1 ? "This link has already been paid." : "This link has reached its limit of payments.";
        return null;
    }

    /// <summary>The payer pressed Pay: create a payment for this link and send them to checkout.</summary>
    public async Task<PaymentResult> PayAsync(TenantRecord tenant, PaymentLink link, long? chosenAmount, string? name, string? email, string? phone, EventSource source)
    {
        if (Closed(tenant, link) is { } closed) return PaymentResult.Fail(400, "link_closed", closed);
        var amount = link.Amount ?? chosenAmount;
        if (amount is null) return PaymentResult.Fail(400, "invalid_request", "Enter the amount to pay.");
        if (link.Amount is null)
        {
            if (link.MinAmount is { } min && amount < min) return PaymentResult.Fail(400, "invalid_request", $"The amount must be at least {Money.Format(min, link.Currency)}.");
            if (link.MaxAmount is { } max && amount > max) return PaymentResult.Fail(400, "invalid_request", $"The amount can be at most {Money.Format(max, link.Currency)}.");
        }
        if (link.AskEmail && string.IsNullOrWhiteSpace(email)) return PaymentResult.Fail(400, "invalid_request", "Enter your email address.");
        if (link.AskPhone && string.IsNullOrWhiteSpace(phone)) return PaymentResult.Fail(400, "invalid_request", "Enter your phone number.");
        var request = new CreatePaymentRequest(amount.Value, link.Currency, null, link.Title.Length > 300 ? link.Title[..300] : link.Title,
            Blank(name), Blank(email), Blank(phone), null, link.ReturnUrl, new Dictionary<string, string> { ["link"] = link.Code }, null);
        return await payments.CreateAsync(tenant, request, link.Livemode, null, source, "link", linkId: link.Id);
    }

    private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
