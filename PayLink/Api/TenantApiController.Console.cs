using Microsoft.AspNetCore.Mvc;
using PayLink.Infrastructure;
using PayLink.Payments;
using PayLink.Platform;
using PayLink.Tenancy;
using QRCoder;
using WebSvc.PayLink.Abstractions;

namespace PayLink.Api;

/// <summary>
/// The rest of the console, for scripts: everything a tenant owner or admin can do on a console page has a route here, each behind
/// the capability that matches the page. The console and these routes call the same services, so rules and audit entries are the same.
/// </summary>
public sealed partial class TenantApiController
{
    private T S<T>() where T : notnull => HttpContext.RequestServices.GetRequiredService<T>();

    // ---------- overview ----------

    /// <summary>What the console's overview shows: the last 7 days, what is waiting, and how far set-up has got.</summary>
    [HttpGet("overview")]
    [RequireTenantCapability(TenantCapabilities.PaymentsRead)]
    public async Task<IActionResult> Overview(bool livemode = false)
    {
        var week = await payments.SummaryAsync(Tenant, livemode, DateTime.UtcNow.AddDays(-7));
        var saved = await methods.ListAsync(Tenant);
        var ll = await settings.GetLoginLinkAsync();
        return Json(new
        {
            tenant = new { Tenant.Slug, Tenant.Name, Tenant.Status, console_mode = Tenant.ConsoleMode, created_at = Tenant.CreatedAt },
            sign_in = new
            {
                loginlink_server = ll?.ServerUrl, organization = Tenant.LoginlinkOrg, issuer = Tenant.LoginlinkIssuer,
                console_client_id = Tenant.ConsoleClientId, management_access = Tenant.MgmtClientId is not null,
            },
            last_7_days = new
            {
                succeeded_count = week.SucceededCount, succeeded_amount = week.SucceededAmount, open = week.OpenCount, failed = week.FailedCount, on_hold = week.OnHoldCount,
            },
            upi_waiting = (await payments.UpiQueueAsync(Tenant, livemode)).Count,
            live_ready = await methods.IsLiveReadyAsync(Tenant),
            methods_on = saved.Where(s => s.Enabled).Select(s => new { connector = s.ConnectorId, mode = s.Mode }),
        });
    }

    // ---------- payments (write) ----------

    public sealed record CreatePaymentBody(long Amount, string? Currency, string? Reference, string? Description, [property: System.Text.Json.Serialization.JsonPropertyName("return_url")] string? ReturnUrl,
        [property: System.Text.Json.Serialization.JsonPropertyName("customer_name")] string? CustomerName, [property: System.Text.Json.Serialization.JsonPropertyName("customer_email")] string? CustomerEmail, [property: System.Text.Json.Serialization.JsonPropertyName("customer_phone")] string? CustomerPhone, [property: System.Text.Json.Serialization.JsonPropertyName("customer_id")] string? CustomerId, Dictionary<string, string>? Metadata, [property: System.Text.Json.Serialization.JsonPropertyName("expires_in_seconds")] int? ExpiresInSeconds);

    /// <summary>Creates a payment (as "New payment" in the console does) and returns its checkout_url to share. Amounts in paise.</summary>
    [HttpPost("payments")]
    [RequireTenantCapability(TenantCapabilities.PaymentsWrite)]
    public async Task<IActionResult> CreatePayment([FromBody] CreatePaymentBody body, bool livemode = false)
    {
        var result = await payments.CreateAsync(Tenant, new CreatePaymentRequest(body.Amount, body.Currency, body.Reference, body.Description,
                body.CustomerName, body.CustomerEmail, body.CustomerPhone, body.CustomerId, body.ReturnUrl, body.Metadata, body.ExpiresInSeconds),
            livemode, Request.Headers["Idempotency-Key"].FirstOrDefault(), new EventSource(Me), "api");
        if (result.Error is { } e) return Error(e.Status, e.Code, e.Message);
        return Json(PaymentJson.From(result.Payment!, await settings.GetPublicBaseUrlAsync()), result.Replayed ? 200 : 201);
    }

    /// <summary>Releases a payment a risk rule put on hold, so the payer can pay.</summary>
    [HttpPost("payments/{id}/release")]
    [RequireTenantCapability(TenantCapabilities.PaymentsWrite)]
    public async Task<IActionResult> Release(string id, [FromBody] NoteBody? body) =>
        Outcome(await payments.ReleaseHoldAsync(Tenant, id, body?.Note, new EventSource(Me)));

    /// <summary>The payments list as CSV (up to 50,000 rows), with the same filters as GET /payments.</summary>
    [HttpGet("payments/export")]
    [RequireTenantCapability(TenantCapabilities.PaymentsRead)]
    public async Task<IActionResult> ExportPayments(bool livemode = false, string? status = null, string? q = null, string? method = null,
        string? from = null, string? to = null, long? min_amount = null, long? max_amount = null, string? reference = null)
    {
        var csv = await S<Reports>().CsvAsync(Tenant, livemode, Filter(status, q, method, from, to, min_amount, max_amount, reference));
        await S<TenantDb>().AuditAsync(Tenant, Me, "report.exported", $"payments through the API, {(livemode ? "live" : "test")}");
        return File(System.Text.Encoding.UTF8.GetBytes(csv), "text/csv", $"payments-{Tenant.Slug}-{(livemode ? "live" : "test")}.csv");
    }

    /// <summary>The payer's UPI screenshot for an attempt. Every view is audited.</summary>
    [HttpGet("payments/{id}/attempts/{attemptId}/proof")]
    [RequireTenantCapability(TenantCapabilities.UpiVerify)]
    public async Task<IActionResult> Proof(string id, string attemptId)
    {
        var attempt = (await payments.AttemptsAsync(Tenant, id)).FirstOrDefault(a => a.Id == attemptId);
        if (attempt?.ProofFile is null || await S<FileStore>().OpenAsync(attempt.ProofFile) is not { } file) return Error(404, "not_found", "No screenshot for that attempt.");
        await S<TenantDb>().AuditAsync(Tenant, Me, "file.viewed", id, $"UPI screenshot for {attemptId}");
        Response.Headers.CacheControl = "private, no-store";
        return File(file.Content, file.ContentType);
    }

    // ---------- payment links ----------

    private async Task<object> LinkJson(PaymentLink l)
    {
        var baseUrl = await S<CustomDomains>().BaseUrlAsync(Tenant);
        return new
        {
            l.Id, l.Livemode, l.Code, url = $"{baseUrl}/l/{l.Code}", l.Title, l.Description, l.Amount, l.MinAmount, l.MaxAmount, l.Currency,
            l.AskEmail, l.AskPhone, l.ReturnUrl, l.MaxPayments, l.ExpiresAt, l.Active, payments_succeeded = l.Paid, amount_collected = l.Collected, l.CreatedAt,
        };
    }

    /// <summary>Payment links, newest first; q searches title and code, state is on or off.</summary>
    [HttpGet("links")]
    [RequireTenantCapability(TenantCapabilities.Links)]
    public async Task<IActionResult> Links(bool livemode = false, string? q = null, string? state = null, int limit = 50, int offset = 0)
    {
        var links = S<PaymentLinkService>();
        var total = await links.CountAsync(Tenant, livemode, q, state);
        var list = await links.ListAsync(Tenant, livemode, q, state, Math.Clamp(limit, 1, 200), Math.Max(0, offset));
        var data = new List<object>();
        foreach (var l in list) data.Add(await LinkJson(l));
        return Json(new { @object = "list", total, data });
    }

    public sealed record LinkBody(string Title, string? Description, long? Amount, [property: System.Text.Json.Serialization.JsonPropertyName("min_amount")] long? MinAmount, [property: System.Text.Json.Serialization.JsonPropertyName("max_amount")] long? MaxAmount, string? Currency,
        [property: System.Text.Json.Serialization.JsonPropertyName("ask_email")] bool AskEmail, [property: System.Text.Json.Serialization.JsonPropertyName("ask_phone")] bool AskPhone, [property: System.Text.Json.Serialization.JsonPropertyName("return_url")] string? ReturnUrl, [property: System.Text.Json.Serialization.JsonPropertyName("max_payments")] int? MaxPayments, [property: System.Text.Json.Serialization.JsonPropertyName("expires_at")] string? ExpiresAt, string? Code);

    /// <summary>Creates a payment link. Amounts in paise; leave amount out to let the payer choose.</summary>
    [HttpPost("links")]
    [RequireTenantCapability(TenantCapabilities.Links)]
    public async Task<IActionResult> CreateLink([FromBody] LinkBody body, bool livemode = false)
    {
        var (link, problem) = await S<PaymentLinkService>().CreateAsync(Tenant, new CreateLinkRequest(body.Title ?? "", body.Description, body.Amount,
            body.MinAmount, body.MaxAmount, body.Currency, body.AskEmail, body.AskPhone, body.ReturnUrl, body.MaxPayments, body.ExpiresAt, body.Code), livemode, Me);
        if (problem is not null) return Error(problem.Contains("taken") ? 409 : 400, problem.Contains("taken") ? "code_taken" : "invalid_request", problem);
        return Json(await LinkJson(link!), 201);
    }

    /// <summary>One payment link.</summary>
    [HttpGet("links/{id}")]
    [RequireTenantCapability(TenantCapabilities.Links)]
    public async Task<IActionResult> Link(string id) =>
        await S<PaymentLinkService>().GetAsync(Tenant, id) is { } l ? Json(await LinkJson(l)) : Error(404, "not_found", $"No link {id}.");

    public sealed record ActiveBody(bool Active);

    /// <summary>Turns a link on or off. A link that's off tells payers it's no longer taking payments.</summary>
    [HttpPatch("links/{id}")]
    [RequireTenantCapability(TenantCapabilities.Links)]
    public async Task<IActionResult> SetLinkActive(string id, [FromBody] ActiveBody body)
    {
        var links = S<PaymentLinkService>();
        if (await links.GetAsync(Tenant, id) is null) return Error(404, "not_found", $"No link {id}.");
        await links.SetActiveAsync(Tenant, id, body.Active, Me);
        return Json(await LinkJson((await links.GetAsync(Tenant, id))!));
    }

    /// <summary>The link's QR code as SVG, for printing.</summary>
    [HttpGet("links/{id}/qr")]
    [RequireTenantCapability(TenantCapabilities.Links)]
    public async Task<IActionResult> LinkQr(string id)
    {
        if (await S<PaymentLinkService>().GetAsync(Tenant, id) is not { } link) return Error(404, "not_found", $"No link {id}.");
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode($"{await S<CustomDomains>().BaseUrlAsync(Tenant)}/l/{link.Code}", QRCodeGenerator.ECCLevel.M);
        return Content(new SvgQRCode(data).GetGraphic(10), "image/svg+xml");
    }

    // ---------- reports ----------

    /// <summary>The Reports page as JSON: funnel, methods, failure reasons and daily volume. A range is at most 366 days.</summary>
    [HttpGet("reports")]
    [RequireTenantCapability(TenantCapabilities.Reports)]
    public async Task<IActionResult> Report(bool livemode = false, string? range = null, string? from = null, string? to = null)
    {
        var dates = !string.IsNullOrEmpty(from) ? DateRange.Parse(null, from, to, maxDays: 366) : DateRange.Parse(range, null, null, maxDays: 366);
        var r = await S<Reports>().BuildAsync(Tenant, livemode, dates);
        return Json(new { from = dates.From.ToString("yyyy-MM-dd"), to = dates.To.ToString("yyyy-MM-dd"), report = r });
    }

    // ---------- reconciliation ----------

    /// <summary>
    /// Imports a gateway's settlement report (CSV, multipart field "report" or a text/csv body). Without a mapping PayLink uses the
    /// columns it recognises (the same guess the console shows); pass column numbers (0-based) to override.
    /// </summary>
    [HttpPost("settlements")]
    [RequireTenantCapability(TenantCapabilities.Reconciliation)]
    [RequestSizeLimit(20 * 1024 * 1024)]
    public async Task<IActionResult> ImportSettlement(string? connector = null, int? reference = null, int? gross = null, int? fee = null, int? tax = null,
        int? net = null, int? date = null, int? settlement_ref = null, bool paise = false)
    {
        var settlements = S<Settlements>();
        Stream stream;
        var name = "settlement.csv";
        if (Request.HasFormContentType)
        {
            var file = (await Request.ReadFormAsync()).Files["report"];
            if (file is not { Length: > 0 }) return Error(400, "invalid_request", "Send the settlement CSV in the form field \"report\".");
            stream = file.OpenReadStream();
            name = file.FileName;
        }
        else stream = Request.Body;
        string? token;
        IReadOnlyList<string> headers;
        string? problem;
        await using (stream) (token, headers, problem) = await settlements.StageAsync(Tenant.Id, stream, name);
        if (problem is not null) return Error(422, "report_unreadable", problem);
        var guess = Settlements.Guess(headers);
        var map = new SettlementMapping(reference ?? guess.Reference, gross ?? guess.Gross, fee ?? guess.Fee, tax ?? guess.Tax, net ?? guess.Net,
            date ?? guess.Date, settlement_ref ?? guess.SettlementRef, paise || guess.AmountsInPaise);
        var (batch, importProblem) = await settlements.ImportAsync(Tenant, token!, connector, map, Me);
        if (importProblem is not null) return Error(422, "report_unreadable", importProblem);
        return Json(new { batch, headers, mapping = map, need_a_look = await settlements.ProblemsAsync(Tenant, batch!.Id) }, 201);
    }

    /// <summary>Imported reports, lines that need a look, the daily close and paid gateway payments not yet settled.</summary>
    [HttpGet("settlements")]
    [RequireTenantCapability(TenantCapabilities.Reconciliation)]
    public async Task<IActionResult> SettlementOverview()
    {
        var s = S<Settlements>();
        var (close, unsettled) = await s.CloseAsync(Tenant);
        var baseUrl = await settings.GetPublicBaseUrlAsync();
        return Json(new
        {
            batches = await s.BatchesAsync(Tenant), need_a_look = await s.ProblemsAsync(Tenant), daily_close = close,
            unsettled = unsettled.Select(p => PaymentJson.From(p, baseUrl)),
        });
    }

    // ---------- disputes ----------

    /// <summary>Disputes reported by gateways.</summary>
    [HttpGet("disputes")]
    [RequireTenantCapability(TenantCapabilities.Disputes)]
    public async Task<IActionResult> Disputes(bool livemode = false) =>
        Json(new { data = (await payments.DisputesAsync(Tenant, livemode)).Select(PaymentJson.From) });

    /// <summary>Saves your note on a dispute (what evidence you sent).</summary>
    [HttpPost("disputes/{id}/note")]
    [RequireTenantCapability(TenantCapabilities.Disputes)]
    public async Task<IActionResult> NoteDispute(string id, [FromBody] NoteBody body) =>
        await payments.NoteDisputeAsync(Tenant, id, body.Note, Me) ? Json(new { id, note = body.Note }) : Error(404, "not_found", $"No dispute {id}.");

    // ---------- routing and account requests ----------

    /// <summary>The checkout order and amount ranges of the methods that are on, which are resting after errors, and failover.</summary>
    [HttpGet("routing")]
    [RequireTenantCapability(TenantCapabilities.Methods)]
    public async Task<IActionResult> Routing(bool livemode = false)
    {
        var mode = livemode ? "live" : "test";
        var saved = await methods.ListAsync(Tenant);
        var tripped = await methods.TrippedAsync(Tenant, livemode);
        return Json(new
        {
            failover = await methods.FailoverOnAsync(Tenant),
            data = saved.Where(s => s.Mode == mode && s.Enabled).OrderBy(s => s.SortOrder).Select(s => new
            {
                connector = s.ConnectorId, order = s.SortOrder, min_amount = s.MinAmount, max_amount = s.MaxAmount, resting = tripped.Contains(s.ConnectorId),
            }),
        });
    }

    public sealed record RouteBody(string Connector, int Order, [property: System.Text.Json.Serialization.JsonPropertyName("min_amount")] long? MinAmount, [property: System.Text.Json.Serialization.JsonPropertyName("max_amount")] long? MaxAmount);
    public sealed record RoutingBody(List<RouteBody>? Methods, bool? Failover);

    /// <summary>Sets the checkout order and amount ranges of methods that are on, and whether failed starts move to the next gateway.</summary>
    [HttpPut("routing")]
    [RequireTenantCapability(TenantCapabilities.Methods)]
    public async Task<IActionResult> SetRouting([FromBody] RoutingBody body, bool livemode = false)
    {
        var mode = livemode ? PaymentMode.Live : PaymentMode.Test;
        foreach (var r in body.Methods ?? [])
        {
            if (registry.Get(r.Connector) is null) return Error(400, "invalid_request", $"No connector {r.Connector}.");
            if (r.MinAmount is { } mn && r.MaxAmount is { } mx && mn > mx) return Error(400, "invalid_request", $"{r.Connector}: min_amount is more than max_amount.");
        }
        foreach (var r in body.Methods ?? [])
            await methods.SetRoutingAsync(Tenant, r.Connector, mode, Math.Clamp(r.Order, 0, 999), r.MinAmount, r.MaxAmount, Me);
        if (body.Failover is { } on) await S<TenantDb>().SetSettingAsync(Tenant, ConnectorSettingsService.FailoverSetting, on ? "on" : "off");
        return await Routing(livemode);
    }

    /// <summary>Gateways the platform helps tenants get an account with, and this tenant's requests.</summary>
    [HttpGet("account-requests")]
    [RequireTenantCapability(TenantCapabilities.Methods)]
    public async Task<IActionResult> AccountRequests()
    {
        var partners = S<PartnerOnboarding>();
        return Json(new
        {
            programs = (await partners.ProgramsAsync()).Values.Where(p => p.IsOn)
                .Select(p => new { connector = p.ConnectorId, kind = p.Mode, headline = p.HeadlineFor(registry.Get(p.ConnectorId)?.Manifest.Name ?? p.ConnectorId), p.Pitch, link = p.LinkFor(Tenant.Slug) }),
            requests = (await partners.ForTenantAsync(Tenant.Id)).Select(r => new
            {
                r.Id, connector = r.ConnectorId, r.Kind, r.Status, note = r.AdminNote, r.ContactName, r.ContactEmail, r.ContactPhone, answers = r.Answers, r.CreatedAt, r.UpdatedAt,
            }),
        });
    }

    public sealed record AccountRequestBody(string Connector, string? Name, string? Email, string? Phone, Dictionary<string, string>? Answers);

    /// <summary>"Get an account": records the request. For a partner-link programme the response carries the link to open.</summary>
    [HttpPost("account-requests")]
    [RequireTenantCapability(TenantCapabilities.Methods)]
    public async Task<IActionResult> RequestAccount([FromBody] AccountRequestBody body)
    {
        var partners = S<PartnerOnboarding>();
        if (registry.Get(body.Connector) is null || await partners.ProgramAsync(body.Connector) is not { IsOn: true } program)
            return Error(404, "not_found", "That method has no account programme.");
        if (program.Mode == PartnerModes.Form && (string.IsNullOrWhiteSpace(body.Name) || string.IsNullOrWhiteSpace(body.Email) || string.IsNullOrWhiteSpace(body.Phone)))
            return Error(400, "invalid_request", "name, email and phone are needed so the team can reach you.");
        var answers = PartnerOnboarding.FormQuestions
            .Select(q => KeyValuePair.Create(q.Label, body.Answers?.GetValueOrDefault(q.Key) ?? ""))
            .Where(a => !string.IsNullOrWhiteSpace(a.Value)).ToList();
        var r = await partners.RecordAsync(Tenant, body.Connector, program.Mode, body.Name ?? Me.Name, body.Email, body.Phone, answers, Me);
        return Json(new { r.Id, r.Status, kind = program.Mode, link = program.Mode == PartnerModes.Link ? program.LinkFor(Tenant.Slug) : null }, 201);
    }

    // ---------- webhooks: test event ----------

    /// <summary>Sends a webhook.test event to an endpoint.</summary>
    [HttpPost("webhooks/{id:long}/test")]
    [RequireTenantCapability(TenantCapabilities.Webhooks)]
    public async Task<IActionResult> TestWebhook(long id)
    {
        if ((await webhooks.EndpointsAsync(Tenant)).All(e => e.Id != id)) return Error(404, "not_found", $"No webhook endpoint {id}.");
        await webhooks.SendTestAsync(Tenant, id);
        return Accepted();
    }

    // ---------- alerts ----------

    /// <summary>Alert kinds, this tenant's alerts and recently sent ones.</summary>
    [HttpGet("alerts")]
    [RequireTenantCapability(TenantCapabilities.Alerts)]
    public async Task<IActionResult> Alerts()
    {
        var alerts = S<AlertService>();
        return Json(new
        {
            kinds = AlertService.Kinds.Select(k => new { k.Kind, k.Label, k.Unit }),
            data = await alerts.RulesAsync(Tenant), recent = await alerts.RecentAsync(Tenant),
        });
    }

    public sealed record AlertBody(string Kind, string Threshold, [property: System.Text.Json.Serialization.JsonPropertyName("window_minutes")] int WindowMinutes = 60, string Channel = "both");

    /// <summary>Adds an alert: kind, threshold, window in minutes, channel (both, webhook or email).</summary>
    [HttpPost("alerts")]
    [RequireTenantCapability(TenantCapabilities.Alerts)]
    public async Task<IActionResult> AddAlert([FromBody] AlertBody body) =>
        await S<AlertService>().AddAsync(Tenant, body.Kind, body.Threshold, body.WindowMinutes, body.Channel, Me) is { } problem
            ? Error(400, "invalid_request", problem) : Json(new { added = true }, 201);

    /// <summary>Removes an alert.</summary>
    [HttpDelete("alerts/{id:long}")]
    [RequireTenantCapability(TenantCapabilities.Alerts)]
    public async Task<IActionResult> RemoveAlert(long id)
    {
        await S<AlertService>().RemoveAsync(Tenant, id, Me);
        return NoContent();
    }

    // ---------- team ----------

    /// <summary>Who may sign in, the members with their roles, and the role map.</summary>
    [HttpGet("team")]
    [RequireTenantCapability(TenantCapabilities.Team)]
    public async Task<IActionResult> Team()
    {
        var db = S<TenantDb>();
        return Json(new
        {
            admission_mode = await db.GetAdmissionModeAsync(Tenant),
            members = (await db.MembersAsync(Tenant)).Select(m => new { m.Id, m.Email, m.Name, m.Role, m.Status, m.Source, m.CreatedAt, m.DecidedBy, m.DecidedAt, m.LastSignInAt }),
            role_map = (await db.RoleMapAsync(Tenant)).Select(r => new { loginlink_role = r.LoginLinkRole, paylink_role = r.PayLinkRole }),
            roles = TenantRoles.All,
        });
    }

    public sealed record ModeBody(string Mode);

    /// <summary>Who may sign in to the console: approve_only, auto_tenant_admins, role or anyone.</summary>
    [HttpPut("team/admission")]
    [RequireTenantCapability(TenantCapabilities.Team)]
    public async Task<IActionResult> SetAdmission([FromBody] ModeBody body)
    {
        if (body.Mode is not (AdmissionModes.ApproveOnly or AdmissionModes.AutoTenantAdmins or AdmissionModes.Role or AdmissionModes.Anyone))
            return Error(400, "invalid_request", "mode is approve_only, auto_tenant_admins, role or anyone.");
        var db = S<TenantDb>();
        await db.SetSettingAsync(Tenant, "admission_mode", body.Mode);
        await db.AuditAsync(Tenant, Me, "team.admission_mode_changed", body.Mode);
        return Json(new { admission_mode = body.Mode });
    }

    public sealed record MemberBody(string Decision, string? Role);

    /// <summary>
    /// Approves a person with a role, changes their role, or removes them. Keys can't make owners or remove the last owner;
    /// owners are made by an owner in the console.
    /// </summary>
    [HttpPost("team/members/{id:long}")]
    [RequireTenantCapability(TenantCapabilities.Team)]
    public async Task<IActionResult> DecideMember(long id, [FromBody] MemberBody body)
    {
        var db = S<TenantDb>();
        if (await db.MemberByIdAsync(Tenant, id) is not { } member) return Error(404, "not_found", $"No member {id}.");
        var lastOwner = member.Role == TenantRoles.Owner && await db.CountOwnersAsync(Tenant) <= 1;
        if (body.Decision == "remove")
        {
            if (lastOwner) return Error(409, "last_owner", "This is the last owner. Make someone else an owner first.");
            member.Status = "removed";
        }
        else if (body.Decision == "approve")
        {
            if (body.Role is null || !TenantRoles.All.Contains(body.Role)) return Error(400, "invalid_request", $"role is one of {string.Join(", ", TenantRoles.All)}.");
            if (body.Role == TenantRoles.Owner && member.Role != TenantRoles.Owner) return Error(403, "owner_only", "Only an owner can make someone an owner, in the console.");
            if (lastOwner && body.Role != TenantRoles.Owner) return Error(409, "last_owner", "This is the last owner. Make someone else an owner first.");
            member.Status = "approved";
            member.Role = body.Role;
            member.Source = "approved";
        }
        else return Error(400, "invalid_request", "decision is approve or remove.");
        member.DecidedBy = Me.Name;
        member.DecidedAt = Clock.Now();
        await db.UpsertMemberAsync(Tenant, member);
        await db.AuditAsync(Tenant, Me, body.Decision == "remove" ? "member.removed" : "member.approved", member.Email ?? member.Subject, member.Role);
        return Json(new { member.Id, member.Email, member.Role, member.Status });
    }

    public sealed record RoleMapBody([property: System.Text.Json.Serialization.JsonPropertyName("loginlink_role")] string LoginlinkRole, [property: System.Text.Json.Serialization.JsonPropertyName("paylink_role")] string? PaylinkRole);

    /// <summary>Maps a LoginLink role to a PayLink role (used by the "role" admission mode). An empty paylink_role removes the mapping.</summary>
    [HttpPut("team/role-map")]
    [RequireTenantCapability(TenantCapabilities.Team)]
    public async Task<IActionResult> SetRoleMap([FromBody] RoleMapBody body)
    {
        var role = (body.LoginlinkRole ?? "").Trim();
        if (role.Length is 0 or > 100) return Error(400, "invalid_request", "loginlink_role is needed.");
        if (!string.IsNullOrEmpty(body.PaylinkRole) && (!TenantRoles.All.Contains(body.PaylinkRole) || body.PaylinkRole == TenantRoles.Owner))
            return Error(400, "invalid_request", "paylink_role is admin, developer, finance or viewer.");
        var db = S<TenantDb>();
        await db.SetRoleMapAsync(Tenant, role, string.IsNullOrEmpty(body.PaylinkRole) ? null : body.PaylinkRole);
        await db.AuditAsync(Tenant, Me, "team.role_map_changed", role, body.PaylinkRole ?? "removed");
        return Json(new { loginlink_role = role, paylink_role = body.PaylinkRole });
    }

    // ---------- domain ----------

    private static object? DomainJson(TenantDomain? d) => d is null ? null : new
    {
        d.Domain, d.Status, verified = d.Verified, d.VerifiedAt, d.CheckedAt, d.LastError,
        txt_record = new { name = d.ChallengeName, value = d.ChallengeValue },
    };

    /// <summary>The tenant's own domain, its DNS records and verification state.</summary>
    [HttpGet("domain")]
    [RequireTenantCapability(TenantCapabilities.Domain)]
    public async Task<IActionResult> Domain()
    {
        var target = (await settings.GetPublicBaseUrlAsync()) is { Length: > 0 } b ? new Uri(b).Host : null;
        return Json(new { domain = DomainJson(await S<CustomDomains>().ForTenantAsync(Tenant.Id)), point_cname_to = target });
    }

    public sealed record DomainBody(string Domain);

    /// <summary>Adds a domain. Add the TXT record returned, point the domain at PayLink, then verify.</summary>
    [HttpPut("domain")]
    [RequireTenantCapability(TenantCapabilities.Domain)]
    public async Task<IActionResult> SetDomain([FromBody] DomainBody body)
    {
        var (added, problem) = await S<CustomDomains>().AddAsync(Tenant, body.Domain, Me);
        return problem is not null ? Error(400, "invalid_request", problem) : Json(new { domain = DomainJson(added) }, 201);
    }

    /// <summary>Checks the TXT record. Answers 422 while it isn't found.</summary>
    [HttpPost("domain/verify")]
    [RequireTenantCapability(TenantCapabilities.Domain)]
    public async Task<IActionResult> VerifyDomain()
    {
        var domains = S<CustomDomains>();
        var (ok, message) = await domains.VerifyAsync(Tenant, Me);
        return Json(new { verified = ok, message, domain = DomainJson(await domains.ForTenantAsync(Tenant.Id)) }, ok ? 200 : 422);
    }

    /// <summary>Removes the domain; the platform's address is used again.</summary>
    [HttpDelete("domain")]
    [RequireTenantCapability(TenantCapabilities.Domain)]
    public async Task<IActionResult> RemoveDomain()
    {
        await S<CustomDomains>().RemoveAsync(Tenant, Me);
        return NoContent();
    }

    // ---------- data access (CrudApi) ----------

    /// <summary>The CrudApi connection, the platform's CrudApi address, and the reporting API path.</summary>
    [HttpGet("data-access")]
    [RequireTenantCapability(TenantCapabilities.DataAccess)]
    public async Task<IActionResult> DataAccess()
    {
        var crud = S<CrudApiConnection>();
        var unavailable = crud.Unavailable(Tenant);
        var l = unavailable is null ? await crud.GetAsync(Tenant) : null;
        return Json(new
        {
            unavailable, crudapi_url = await settings.GetCrudApiUrlAsync(), reporting_api = "/api/v1/reporting",
            link = l is null ? null : new
            {
                read_user = l.ReadUser, l.Host, l.Port, l.Database, l.Server, l.Workspace, registration_id = l.RegistrationId,
                reader_client_id = l.ReaderClientId, l.Note, l.CreatedAt, created_by_paylink = l.CreatedUserHost is not null,
            },
        });
    }

    public sealed record ReaderBody(string? User, string? Password, string? Host);

    /// <summary>Step 1: a read-only database user (PayLink creates one when it can; otherwise send yours, which is checked to be SELECT-only).</summary>
    [HttpPut("data-access/reader")]
    [RequireTenantCapability(TenantCapabilities.DataAccess)]
    public async Task<IActionResult> SetReader([FromBody] ReaderBody body) =>
        await S<CrudApiConnection>().SetReaderAsync(Tenant, body.User, body.Password, body.Host, Me) is { } problem
            ? Error(422, "reader_refused", problem) : await DataAccess();

    public sealed record RegisterBody(string? Server, string? Workspace, string? Key);

    /// <summary>Step 2: registers the database in a CrudApi workspace and creates a Reader client. Its secret is returned once.</summary>
    [HttpPost("data-access/register")]
    [RequireTenantCapability(TenantCapabilities.DataAccess)]
    public async Task<IActionResult> RegisterCrudApi([FromBody] RegisterBody body)
    {
        var (ok, message, secret) = await S<CrudApiConnection>().RegisterAsync(Tenant, body.Server ?? await settings.GetCrudApiUrlAsync(), body.Workspace, body.Key, Me);
        return ok ? Json(new { message, reader_client_secret = secret }, 201) : Error(422, "crudapi_refused", message);
    }

    /// <summary>Unregisters the database from CrudApi and removes the read-only user PayLink made.</summary>
    [HttpDelete("data-access")]
    [RequireTenantCapability(TenantCapabilities.DataAccess)]
    public async Task<IActionResult> DisconnectCrudApi() => Json(new { message = await S<CrudApiConnection>().DisconnectAsync(Tenant, Me) });

    // ---------- keys ----------

    /// <summary>The tenant's keys (secrets never returned).</summary>
    [HttpGet("keys")]
    [RequireTenantCapability(TenantCapabilities.Keys)]
    public async Task<IActionResult> Keys() =>
        Json(new
        {
            data = (await S<TenantKeyService>().ListAsync(Tenant)).Select(k => new
            {
                k.Id, k.Name, k.Kind, k.Livemode, prefix = k.KeyPrefix, capabilities = k.CapabilityList, k.CreatedAt, k.CreatedBy, k.LastUsedAt, k.ExpiresAt, k.RevokedAt,
            }),
        });

    public sealed record KeyBody(string Name, string Kind, bool Livemode, List<string>? Capabilities, [property: System.Text.Json.Serialization.JsonPropertyName("expires_at")] string? ExpiresAt);

    /// <summary>
    /// Creates a secret key (kind "secret", for /api/v1) or a developer key (kind "developer", for /api/tenant). A developer key
    /// can only be given capabilities the calling key has. The key is returned once.
    /// </summary>
    [HttpPost("keys")]
    [RequireTenantCapability(TenantCapabilities.Keys)]
    public async Task<IActionResult> CreateKey([FromBody] KeyBody body)
    {
        if (string.IsNullOrWhiteSpace(body.Name)) return Error(400, "invalid_request", "name is needed.");
        if (body.Kind is not (TenantKeyKinds.Secret or TenantKeyKinds.Developer)) return Error(400, "invalid_request", "kind is secret or developer.");
        var caps = body.Capabilities ?? [];
        if (body.Kind == TenantKeyKinds.Developer)
        {
            if (caps.Count == 0) return Error(400, "invalid_request", "A developer key needs at least one capability.");
            if (caps.FirstOrDefault(c => !TenantCapabilities.IsKnown(c)) is { } unknown) return Error(400, "invalid_request", $"Unknown capability {unknown}.");
            var mine = User.FindAll(TenantKeyHandler.CapabilityClaim).Select(c => c.Value).ToHashSet();
            if (caps.FirstOrDefault(c => !mine.Contains(c)) is { } extra) return Error(403, "insufficient_scope", $"This key can't give the '{extra}' capability it doesn't have.");
        }
        var (key, secret) = await S<TenantKeyService>().CreateAsync(Tenant, body.Kind, body.Name, body.Livemode, caps, body.ExpiresAt, Me);
        return Json(new { key.Id, key.Name, key.Kind, key.Livemode, prefix = key.KeyPrefix, capabilities = key.CapabilityList, secret }, 201);
    }

    /// <summary>Revokes a key. Calls with it answer 401 from then on.</summary>
    [HttpDelete("keys/{id:long}")]
    [RequireTenantCapability(TenantCapabilities.Keys)]
    public async Task<IActionResult> RevokeKey(long id) =>
        await S<TenantKeyService>().RevokeAsync(Tenant, id, Me) ? NoContent() : Error(404, "not_found", $"No active key {id}.");

    /// <summary>The API request log: the last calls made with this tenant's keys (no bodies).</summary>
    [HttpGet("requests")]
    [RequireTenantCapability(TenantCapabilities.Keys)]
    public async Task<IActionResult> RequestLog(int limit = 100) => Json(new { data = await S<TenantKeyService>().RequestsAsync(Tenant, Math.Clamp(limit, 1, 500)) });

    // ---------- audit ----------

    /// <summary>The tenant's audit log, newest first. verify=true also checks the hash chain.</summary>
    [HttpGet("audit")]
    [RequireTenantCapability(TenantCapabilities.Audit)]
    public async Task<IActionResult> Audit(int limit = 200, bool verify = false)
    {
        await using var c = await S<TenantDb>().OpenAsync(Tenant);
        var entries = await AuditLog.RecentAsync(c, Math.Clamp(limit, 1, 1000));
        long? brokenAt = verify ? await AuditLog.VerifyAsync(c) : null;
        return Json(new { data = entries, chain = verify ? (brokenAt is null ? "intact" : $"broken at {brokenAt}") : null });
    }
}
