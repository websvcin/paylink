using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using PayLink.Infrastructure;
using PayLink.Payments;
using PayLink.Platform;
using PayLink.Tenancy;
using PayLink.Tests.Fakes;
using Xunit;
using static PayLink.Tests.Flows;

namespace PayLink.Tests;

/// <summary>Phase 3, insight: risk score and location rules, reports, alerts, settlement reconciliation.</summary>
public sealed class InsightTests
{
    private static readonly FakeLoginLink.Person Priya = new("sub-priya", "priya@acme.in", "Priya", "tenant_admin", []);

    private sealed class FakeGeo : IGeoLocator
    {
        public string? CountryValue { get; set; }
        public bool Tor { get; set; }
        public string? Country(string? ip) => CountryValue;
        public bool IsTor(string? ip) => Tor;
    }

    private sealed class FakeEmail : IEmailSender
    {
        public List<(IReadOnlyCollection<string> To, string Subject, string Body)> Sent { get; } = [];
        public Task<bool> SendAsync(IReadOnlyCollection<string> to, string subject, string body, CancellationToken ct = default)
        {
            Sent.Add((to, subject, body));
            return Task.FromResult(true);
        }
    }

    private static async Task<(PayLinkApp App, FakeLoginLink LL, Browser Owner, TenantRecord Tenant)> StartAsync(Action<IServiceCollection>? services = null)
    {
        var ll = await FakeLoginLink.StartAsync();
        var app = new PayLinkApp(services: services);
        await SetupAsync(app, new Browser(app), ll);
        var owner = await CreateTenantAsync(app, ll, "acme", "Acme Learning", Priya);
        return (app, ll, owner, (await app.Get<PlatformRepository>().TenantBySlugAsync("acme"))!);
    }

    private static Task<PaymentResult> Pay(PayLinkApp app, TenantRecord t, long amount, string? phone = null, string? email = null, bool live = false) =>
        app.Get<PaymentService>().CreateAsync(t, new CreatePaymentRequest(amount, null, null, null, null, email, phone, null, null, null, null), live, null, EventSource.System, "api");

    [Fact]
    public async Task Location_rules_and_the_risk_score_apply_at_checkout()
    {
        var geo = new FakeGeo { CountryValue = "RU" };
        var (app, ll, priya, tenant) = await StartAsync(s => s.AddSingleton<IGeoLocator>(geo));
        await using var _ = ll;
        await using var __ = app;
        await priya.SubmitAsync("/acme/console/risk", "Add", F(("kind", "country_block"), ("value", "RU, KP"), ("action", "block")));
        var blocked = (await Pay(app, tenant, 1000)).Payment!;
        await new Browser(app).SubmitAsync($"/pay/{blocked.Id}", "Choose", F(("connector", "simulator")));
        Assert.Equal(FailureReasons.RiskBlocked, (await app.Get<PaymentService>().GetAsync(tenant, blocked.Id))!.FailureReason);

        // An Indian phone paying from abroad, on a throwaway email, through Tor: a high score, held for review.
        geo.CountryValue = "US";
        geo.Tor = true;
        await priya.SubmitAsync("/acme/console/risk", "Add", F(("kind", "score_above"), ("value", "60"), ("action", "review")));
        var risky = (await Pay(app, tenant, 2000, phone: "+91 98123 45678", email: "x@mailinator.com")).Payment!;
        var payer = new Browser(app);
        await payer.SubmitAsync($"/pay/{risky.Id}", "Choose", F(("connector", "simulator")));
        var held = (await app.Get<PaymentService>().GetAsync(tenant, risky.Id))!;
        Assert.Equal(PaymentStatuses.OnHold, held.Status);
        var detail = await app.Get<PaymentService>().DetailAsync(tenant, risky.Id);
        var scored = detail!.Value.Events.Single(e => e.Type == "risk.scored");
        var data = JsonNode.Parse(scored.Data!)!;
        Assert.True((int)data["score"]! > 60);
        var signals = data["signals"]!.AsArray().Select(x => (string)x!).ToList();
        Assert.Contains("tor", signals);
        Assert.Contains("country_mismatch", signals);
        Assert.Contains("disposable_email", signals);
        Assert.Contains("new_device", signals);
        Assert.Contains("Risk score", await priya.TextAsync(await priya.GetAsync($"/acme/console/payments/{risky.Id}")));

        // Unknown location: country rules fail open.
        geo.CountryValue = null;
        geo.Tor = false;
        var unknown = (await Pay(app, tenant, 1500)).Payment!;
        await new Browser(app).SubmitAsync($"/pay/{unknown.Id}", "Choose", F(("connector", "simulator")));
        Assert.Equal(PaymentStatuses.RequiresAction, (await app.Get<PaymentService>().GetAsync(tenant, unknown.Id))!.Status);
    }

    [Fact]
    public async Task Reports_show_the_funnel_and_export_payments()
    {
        var (app, ll, priya, tenant) = await StartAsync();
        await using var _ = ll;
        await using var __ = app;
        var svc = app.Get<PaymentService>();
        for (var i = 0; i < 3; i++)
        {
            var p = (await Pay(app, tenant, 10000 + i)).Payment!;
            var payer = new Browser(app);
            await payer.GetAsync($"/pay/{p.Id}");
            await payer.SubmitAsync($"/pay/{p.Id}", "Choose", F(("connector", "simulator")));
            var a = (await svc.AttemptsAsync(tenant, p.Id)).Single();
            await payer.SubmitAsync($"/pay/{p.Id}", "Simulate", F(("attemptId", a.Id), ("outcome", i == 0 ? "fail" : "succeed")));
        }
        await Pay(app, tenant, 999);   // never opened

        var html = await priya.TextAsync(await priya.GetAsync("/acme/console/reports?days=7"));
        Assert.Contains("₹200.03", html);   // 10001 + 10002 paise
        Assert.Contains("bank_declined", html);
        Assert.Contains("50%", html);       // 2 of 4 created
        var csv = await priya.GetAsync("/acme/console/reports?handler=Csv&days=7");
        Assert.Equal("text/csv", csv.Content.Headers.ContentType?.MediaType);
        var lines = (await csv.Content.ReadAsStringAsync()).Trim().Split('\n');
        Assert.Equal(5, lines.Length);
        Assert.StartsWith("id,created_at,status,amount", lines[0]);
    }

    [Fact]
    public async Task Alerts_fire_once_by_webhook_and_email()
    {
        var mail = new FakeEmail();
        var (app, ll, priya, tenant) = await StartAsync(s => s.AddSingleton<IEmailSender>(mail));
        await using var _ = ll;
        await using var __ = app;
        await using var hooks = await WebhookReceiver.StartAsync();
        await priya.SubmitAsync("/acme/console?mode=live", null, F());
        await priya.GetAsync("/acme/console?mode=live");
        await priya.SubmitAsync("/acme/console/developers", "Endpoint", F(("url", hooks.Url.Replace("http://", "https://"))));
        await app.Get<WebhookService>().AddEndpointAsync(tenant, hooks.Url, true, null, null, Actor.System);
        var r = await priya.FollowAsync(await priya.SubmitAsync("/acme/console/alerts", "Add", F(("kind", "refund_above"), ("threshold", "100"), ("windowMinutes", "60"), ("channel", "both"))));
        Assert.Contains("Alert added", await priya.TextAsync(r));

        await using (var c = await app.Get<TenantDb>().OpenAsync(tenant))
            await c.ExecuteAsync("INSERT INTO refunds (id, payment_id, attempt_id, livemode, amount, currency, status, connector, manual, created_at, updated_at) " +
                                 "VALUES ('rf_big', 'pay_x', 'att_x', TRUE, 50000, 'INR', 'succeeded', 'upi', TRUE, @now, @now);", new { now = Clock.Now() });
        Assert.Equal(1, await app.Get<AlertService>().EvaluateAsync(tenant));
        Assert.Equal(0, await app.Get<AlertService>().EvaluateAsync(tenant));   // cooldown
        await app.Get<PaymentJobs>().RunOnceAsync();
        var alert = Assert.Single(hooks.Received, d => d.EventType == "alert.triggered");
        Assert.Contains("₹500.00", (string?)alert.Json["data"]!["message"]);
        var email = Assert.Single(mail.Sent);
        Assert.Contains("priya@acme.in", email.To);
        Assert.Contains("emailed", await priya.TextAsync(await priya.GetAsync("/acme/console/alerts")));
    }

    [Fact]
    public async Task Settlement_reports_are_matched_to_payments()
    {
        var (app, ll, priya, tenant) = await StartAsync();
        await using var _ = ll;
        await using var __ = app;
        var svc = app.Get<PaymentService>();
        var ids = new List<string>();
        foreach (var amount in new long[] { 149900, 50000 })
        {
            var p = (await Pay(app, tenant, amount)).Payment!;
            await new Browser(app).SubmitAsync($"/pay/{p.Id}", "Choose", F(("connector", "simulator")));
            var a = (await svc.AttemptsAsync(tenant, p.Id)).Single();
            await new Browser(app).SubmitAsync($"/pay/{p.Id}", "Simulate", F(("attemptId", a.Id), ("outcome", "succeed")));
            await using var c = await app.Get<TenantDb>().OpenAsync(tenant);
            await c.ExecuteAsync("UPDATE attempts SET provider_payment_id = @pid WHERE id = @Id;", new { pid = "pay_RZP" + amount, a.Id });
            ids.Add(p.Id);
        }
        var csv = "Payment ID,Amount,Fee,Tax,Settled At,Settlement ID\n" +
                  "pay_RZP149900,\"1,499.00\",29.98,5.40,2026-10-20,setl_1\n" +
                  "pay_RZP50000,450.00,9.00,1.62,2026-10-20,setl_1\n" +
                  "pay_UNKNOWN,100.00,2.00,0.36,2026-10-20,setl_1\n";
        var r = await priya.SubmitWithFileAsync("/acme/console/reconciliation", "Upload", F(), "report", "rzp.csv", Encoding.UTF8.GetBytes(csv), "text/csv");
        var mapping = r.Headers.Location!.ToString();
        Assert.Contains("mapping=", mapping);
        var html = await priya.TextAsync(await priya.GetAsync(mapping));
        Assert.Contains("<option value=\"0\" selected>Payment ID</option>", html);
        var token = mapping[(mapping.IndexOf("mapping=") + 8)..];
        r = await priya.FollowAsync(await priya.SubmitAsync(mapping, "Import", F(("token", token), ("connector", "razorpay"),
            ("reference", "0"), ("gross", "1"), ("fee", "2"), ("tax", "3"), ("net", "-1"), ("date", "4"), ("settlementRef", "5"))));
        html = await priya.TextAsync(r);
        Assert.Contains("3 lines: 1 matched, 1 need a look, 1 not found", html);
        Assert.Contains("PayLink has ₹500.00, the report ₹450.00", html);
        Assert.Contains("₹1,463.62", html);   // daily close net: 1499.00 - 29.98 - 5.40
        var settled = (await svc.GetAsync(tenant, ids[0]))!;
        Assert.Equal("2026-10-20", settled.SettledAt);
        Assert.Equal("setl_1", settled.SettlementRef);
        Assert.Equal(3538, settled.SettlementFee);
    }

    [Fact]
    public async Task Admins_set_up_email_and_see_location_status()
    {
        await using var ll = await FakeLoginLink.StartAsync();
        await using var app = new PayLinkApp();
        var admin = new Browser(app);
        await SetupAsync(app, admin, ll);
        Assert.Contains("Location data", await admin.TextAsync(await admin.GetAsync("/admin/location")));
        var bad = await admin.FollowAsync(await admin.SubmitAsync("/admin/settings", "Smtp", F(("host", "smtp.example.in"), ("port", "587"), ("from", "nope"))));
        Assert.Contains("Enter the From address", await admin.TextAsync(bad));
        await admin.SubmitAsync("/admin/settings", "Smtp", F(("host", "smtp.example.in"), ("port", "587"), ("ssl", "true"), ("user", "alerts"), ("password", "s3cret"), ("from", "alerts@example.in")));
        var saved = (await app.Get<SmtpEmailSender>().GetAsync())!;
        Assert.Equal(("smtp.example.in", "alerts@example.in", "s3cret"), (saved.Host, saved.From, saved.Password));
        // Saving again without the password keeps it.
        await admin.SubmitAsync("/admin/settings", "Smtp", F(("host", "smtp.example.in"), ("port", "2525"), ("from", "alerts@example.in")));
        saved = (await app.Get<SmtpEmailSender>().GetAsync())!;
        Assert.Equal((2525, "s3cret"), (saved.Port, saved.Password));
    }
}
