using System.Text.Json;
using Dapper;
using PayLink.Infrastructure;

namespace PayLink.Platform;

/// <summary>How the platform helps a tenant get an account with a payment method's provider.</summary>
public static class PartnerModes
{
    /// <summary>No programme: tenants only see "Set up" for the method.</summary>
    public const string Off = "off";
    /// <summary>A referral link to the partner's own signup; PayLink records the interest and sends the tenant there.</summary>
    public const string Link = "link";
    /// <summary>A short form; the platform team follows up and onboards the tenant.</summary>
    public const string Form = "form";
}

public static class PartnerRequestStatuses
{
    public const string New = "new";
    public const string InProgress = "in_progress";
    public const string Done = "done";
    public const string Declined = "declined";

    public static readonly (string Key, string Label)[] All = [(New, "New"), (InProgress, "In progress"), (Done, "Account ready"), (Declined, "Declined")];
    public static string Label(string status) => All.FirstOrDefault(s => s.Key == status).Label ?? status;
    public static string Tone(string status) => status switch { Done => "good", Declined => "bad", InProgress => "info", _ => "warn" };
}

public sealed class PartnerProgram
{
    public string ConnectorId { get; set; } = "";
    public string Mode { get; set; } = PartnerModes.Off;
    public string? Headline { get; set; }
    public string? Pitch { get; set; }
    /// <summary>For <see cref="PartnerModes.Link"/>. <c>{tenant}</c> is replaced with the tenant's slug, so the partner can attribute the signup.</summary>
    public string? LinkUrl { get; set; }
    public string UpdatedAt { get; set; } = "";
    public string? UpdatedBy { get; set; }

    public bool IsOn => Mode is PartnerModes.Link or PartnerModes.Form;
    public string HeadlineFor(string methodName) => string.IsNullOrWhiteSpace(Headline) ? $"No {methodName} account yet? Get one through us" : Headline;
    public string? LinkFor(string tenantSlug) => LinkUrl?.Replace("{tenant}", Uri.EscapeDataString(tenantSlug), StringComparison.OrdinalIgnoreCase);
}

public sealed class PartnerRequest
{
    public long Id { get; set; }
    public long TenantId { get; set; }
    public string ConnectorId { get; set; } = "";
    public string Kind { get; set; } = "";
    public string Status { get; set; } = PartnerRequestStatuses.New;
    public string? ContactName { get; set; }
    public string? ContactEmail { get; set; }
    public string? ContactPhone { get; set; }
    public string? Details { get; set; }
    public string? AdminNote { get; set; }
    public string CreatedAt { get; set; } = "";
    public string? CreatedBy { get; set; }
    public string? UpdatedAt { get; set; }
    public string? UpdatedBy { get; set; }

    /// <summary>The form's answers beyond the contact (business, website, volume, note), in the order asked.</summary>
    public IReadOnlyList<KeyValuePair<string, string>> Answers =>
        string.IsNullOrEmpty(Details) ? [] : JsonSerializer.Deserialize<List<KeyValuePair<string, string>>>(Details) ?? [];
}

/// <summary>
/// Partner onboarding: per payment method, the platform can offer tenants help getting an account (a referral link or a form).
/// Every "Get an account" is recorded against the tenant, so the platform team can see who asked for what and follow up.
/// </summary>
public sealed class PartnerOnboarding(PlatformDb platformDb)
{
    /// <summary>The questions the built-in form asks, after the contact details. Kept short on purpose.</summary>
    public static readonly (string Key, string Label, string Hint)[] FormQuestions =
    [
        ("business", "Registered business name", "As on PAN or GST"),
        ("website", "Website or app", "https://…"),
        ("volume", "Expected monthly volume (₹)", "e.g. 5,00,000"),
        ("note", "Anything we should know", "Business type, current gateway, timelines"),
    ];

    public async Task<IReadOnlyDictionary<string, PartnerProgram>> ProgramsAsync()
    {
        await using var c = await platformDb.OpenAsync();
        return (await c.QueryAsync<PartnerProgram>("SELECT * FROM partner_programs;")).ToDictionary(p => p.ConnectorId);
    }

    public async Task<PartnerProgram> ProgramAsync(string connectorId) =>
        (await ProgramsAsync()).GetValueOrDefault(connectorId) ?? new PartnerProgram { ConnectorId = connectorId };

    public static string? Problem(string mode, string? linkUrl)
    {
        if (mode is not (PartnerModes.Off or PartnerModes.Link or PartnerModes.Form)) return "Choose off, a partner link or a form.";
        if (mode == PartnerModes.Link && !(Uri.TryCreate(linkUrl?.Replace("{tenant}", "x", StringComparison.OrdinalIgnoreCase), UriKind.Absolute, out var u) && u.Scheme == "https"))
            return "Enter the partner's signup link as a full https:// address.";
        return null;
    }

    public async Task<string?> SaveProgramAsync(string connectorId, string mode, string? headline, string? pitch, string? linkUrl, Actor actor)
    {
        linkUrl = string.IsNullOrWhiteSpace(linkUrl) ? null : linkUrl.Trim();
        if (Problem(mode, linkUrl) is { } problem) return problem;
        await using var c = await platformDb.OpenAsync();
        var dialect = await platformDb.DialectAsync();
        await c.ExecuteAsync(dialect.Upsert("partner_programs", "connector_id", "mode", "headline", "pitch", "link_url", "updated_at", "updated_by"), new
        {
            connector_id = connectorId, mode, headline = Trim(headline, 120), pitch = Trim(pitch, 500), link_url = linkUrl,
            updated_at = Clock.Now(), updated_by = actor.Name,
        });
        await AuditLog.WriteAsync(c, actor, "partner.program_saved", connectorId, mode == PartnerModes.Link ? $"link {linkUrl}" : mode);
        return null;
    }

    /// <summary>Records a tenant's request. A second request for the same method while one is still open updates that one instead.</summary>
    public async Task<PartnerRequest> RecordAsync(TenantRecord tenant, string connectorId, string kind, string? name, string? email, string? phone,
        IReadOnlyList<KeyValuePair<string, string>>? answers, Actor actor)
    {
        await using var c = await platformDb.OpenAsync();
        var details = answers is { Count: > 0 } ? JsonSerializer.Serialize(answers.Select(a => KeyValuePair.Create(a.Key, Trim(a.Value, 1000) ?? ""))) : null;
        var open = await c.QueryFirstOrDefaultAsync<PartnerRequest>(
            "SELECT * FROM partner_requests WHERE tenant_id = @tenantId AND connector_id = @connectorId AND status IN ('new', 'in_progress') ORDER BY id DESC;",
            new { tenantId = tenant.Id, connectorId });
        var now = Clock.Now();
        if (open is not null)
        {
            await c.ExecuteAsync(
                "UPDATE partner_requests SET kind = @kind, contact_name = COALESCE(@name, contact_name), contact_email = COALESCE(@email, contact_email), " +
                "contact_phone = COALESCE(@phone, contact_phone), details = COALESCE(@details, details), updated_at = @now, updated_by = @by WHERE id = @id;",
                new { kind, name = Trim(name, 200), email = Trim(email, 320), phone = Trim(phone, 40), details, now, by = actor.Name, id = open.Id });
            await AuditLog.WriteAsync(c, actor, "partner.request_updated", $"{tenant.Slug}/{connectorId}", kind);
            return (await c.QueryFirstAsync<PartnerRequest>("SELECT * FROM partner_requests WHERE id = @id;", new { id = open.Id }));
        }
        await c.ExecuteAsync(
            "INSERT INTO partner_requests (tenant_id, connector_id, kind, status, contact_name, contact_email, contact_phone, details, created_at, created_by) " +
            "VALUES (@tenantId, @connectorId, @kind, 'new', @name, @email, @phone, @details, @now, @by);",
            new { tenantId = tenant.Id, connectorId, kind, name = Trim(name, 200), email = Trim(email, 320), phone = Trim(phone, 40), details, now, by = actor.Name });
        var id = await c.ExecuteScalarAsync<long>((await platformDb.DialectAsync()).LastInsertId);
        await AuditLog.WriteAsync(c, actor, "partner.requested", $"{tenant.Slug}/{connectorId}", kind);
        return await c.QueryFirstAsync<PartnerRequest>("SELECT * FROM partner_requests WHERE id = @id;", new { id });
    }

    public async Task<IReadOnlyList<PartnerRequest>> ForTenantAsync(long tenantId)
    {
        await using var c = await platformDb.OpenAsync();
        return (await c.QueryAsync<PartnerRequest>("SELECT * FROM partner_requests WHERE tenant_id = @tenantId ORDER BY id DESC;", new { tenantId })).ToList();
    }

    public async Task<IReadOnlyList<PartnerRequest>> ListAsync(string? status, string? connectorId, int take = 200)
    {
        await using var c = await platformDb.OpenAsync();
        var where = new List<string>();
        if (!string.IsNullOrEmpty(status)) where.Add("status = @status");
        if (!string.IsNullOrEmpty(connectorId)) where.Add("connector_id = @connectorId");
        var sql = "SELECT * FROM partner_requests" + (where.Count > 0 ? " WHERE " + string.Join(" AND ", where) : "") + " ORDER BY id DESC";
        return (await c.QueryAsync<PartnerRequest>(sql + $" LIMIT {Math.Clamp(take, 1, 1000)};", new { status, connectorId })).ToList();
    }

    public async Task<int> OpenCountAsync()
    {
        await using var c = await platformDb.OpenAsync();
        return await c.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM partner_requests WHERE status = 'new';");
    }

    public async Task<bool> UpdateAsync(long id, string status, string? note, Actor actor)
    {
        if (PartnerRequestStatuses.All.All(s => s.Key != status)) return false;
        await using var c = await platformDb.OpenAsync();
        var n = await c.ExecuteAsync("UPDATE partner_requests SET status = @status, admin_note = @note, updated_at = @now, updated_by = @by WHERE id = @id;",
            new { id, status, note = Trim(note, 1000), now = Clock.Now(), by = actor.Name });
        if (n > 0) await AuditLog.WriteAsync(c, actor, "partner.request_status", id.ToString(), status);
        return n > 0;
    }

    private static string? Trim(string? value, int max) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim() is var t && t.Length > max ? t[..max] : value.Trim();
}
