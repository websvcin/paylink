using System.Net;
using PayLink.Infrastructure;
using PayLink.Platform;
using PayLink.Tenancy;
using PayLink.Tests.Fakes;
using Xunit;
using static PayLink.Tests.Flows;

namespace PayLink.Tests;

/// <summary>End to end on SQLite: setup, LoginLink sign-in, tenant signup, admission, team approval, audit.</summary>
public sealed class PlatformFlowTests
{
    private static readonly FakeLoginLink.Person Priya = new("sub-priya", "priya@acme.in", "Priya", "tenant_admin", []);
    private static readonly FakeLoginLink.Person Ravi = new("sub-ravi", "ravi@acme.in", "Ravi", "end_user", []);

    [Fact]
    public async Task Everything_redirects_to_setup_until_it_is_done()
    {
        await using var app = new PayLinkApp();
        var browser = new Browser(app);

        var home = await browser.GetAsync("/");
        Assert.Equal(HttpStatusCode.Redirect, home.StatusCode);
        Assert.Equal("/setup", home.Headers.Location?.OriginalString);

        var api = await browser.Api.GetAsync("/api/admin/tenants");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, api.StatusCode);

        var health = await browser.GetAsync("/healthz");
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
    }

    [Fact]
    public async Task Wrong_setup_code_is_refused()
    {
        await using var app = new PayLinkApp();
        var browser = new Browser(app);
        var r = await browser.SubmitAsync("/setup", "Unlock", F(("Code", "WRONG")));
        Assert.Contains("doesn&#x27;t match", await browser.TextAsync(r));
    }

    [Fact]
    public async Task Setup_signup_admission_and_audit_work_end_to_end()
    {
        await using var ll = await FakeLoginLink.StartAsync();
        await using var app = new PayLinkApp();
        var admin = new Browser(app);
        await SetupAsync(app, admin, ll);

        // Setup is closed once finished.
        Assert.Equal("/admin", (await admin.GetAsync("/setup")).Headers.Location?.OriginalString);
        // The PayLink admin app was registered in the platform organization with tenant admins allowed.
        Assert.Contains(ll.AppPatches.Values, p => (bool?)p["allowTenantAdminSignIn"] == true);

        // Priya connects her existing LoginLink organization and creates a tenant on SQLite.
        var priya = new Browser(app);
        var html = await ConnectExistingAsync(priya, ll, "acme", Priya);
        Assert.Contains("Name and address", html);
        Assert.Contains("http://localhost", ll.RegisteredApis["acme"]);

        var r = await priya.FollowAsync(await priya.SubmitAsync("/signup", "Create",
            F(("TenantName", "Acme Learning"), ("TenantSlug", "acme"), ("DbChoice", "sqlite"), ("action", "create"))));
        html = await priya.TextAsync(r);
        Assert.Equal("/acme/console", priya.LastUrl!.AbsolutePath);
        Assert.Contains("Acme Learning is ready", html);

        var tenant = (await app.Get<PlatformRepository>().TenantBySlugAsync("acme"))!;
        Assert.Equal("sqlite", tenant.DbMode);
        Assert.Equal("ok", tenant.ApiRegistration);
        Assert.True(File.Exists(app.Get<TenantDb>().Target(tenant).SqliteFilePath));

        // The same organization can't be connected twice.
        var other = new Browser(app);
        var cred = ll.AddManagementCredential("acme");
        r = await other.SubmitAsync("/signup", "ConnectExisting", F(("OrgSlug", "acme"), ("MgmtClientId", cred.ClientId), ("MgmtClientSecret", cred.Secret)));
        Assert.Contains("already connected", await other.TextAsync(r));

        // Ravi is an ordinary member of acme: with the default mode he waits for approval.
        var ravi = new Browser(app);
        var (_, waiting) = await ConsoleSignInAsync(ravi, ll, "acme", "acme", Ravi);
        Assert.Contains("not yet allowed", waiting);
        Assert.Equal(HttpStatusCode.Redirect, (await ravi.GetAsync("/acme/console")).StatusCode);

        // Priya approves him as finance from the Team page.
        var member = (await app.Get<TenantDb>().MemberBySubjectAsync(tenant, Ravi.Subject))!;
        Assert.Equal("pending", member.Status);
        r = await priya.SubmitAsync("/acme/console/team", "Member", F(("id", member.Id.ToString()), ("decision", "approve"), ("role", "finance")));
        Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);

        var (_, console) = await ConsoleSignInAsync(ravi, ll, "acme", "acme", Ravi);
        Assert.Equal("/acme/console", ravi.LastUrl!.AbsolutePath);
        Assert.Contains("ravi@acme.in · finance", console);

        // Finance can't change the team.
        r = await ravi.FollowAsync(await ravi.SubmitAsync("/acme/console/team", "Mode", F(("mode", "anyone"))));
        Assert.Contains("Only owners and admins", await ravi.TextAsync(r));

        // A platform admin suspends the tenant; its team is signed out on the next request.
        r = await admin.SubmitAsync("/admin/tenants", "Status", F(("id", tenant.Id.ToString()), ("status", "suspended")));
        Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
        Assert.Equal("/acme/login", (await ravi.GetAsync("/acme/console")).Headers.Location?.OriginalString);

        // Both audit chains are intact and recorded what happened.
        await using (var c = await app.Get<PlatformDb>().OpenAsync())
        {
            Assert.Null(await AuditLog.VerifyAsync(c));
            var actions = (await AuditLog.RecentAsync(c, 100)).Select(e => e.Action).ToList();
            Assert.Contains("setup.completed", actions);
            Assert.Contains("tenant.created", actions);
            Assert.Contains("tenant.suspended", actions);
        }
        await using (var c = await app.Get<TenantDb>().OpenAsync(tenant))
        {
            Assert.Null(await AuditLog.VerifyAsync(c));
            var actions = (await AuditLog.RecentAsync(c, 100)).Select(e => e.Action).ToList();
            Assert.Contains("member.access_requested", actions);
            Assert.Contains("member.approved", actions);
            Assert.Contains("member.signed_in", actions);
        }
    }

    [Fact]
    public async Task Only_an_organization_admin_can_connect_it()
    {
        await using var ll = await FakeLoginLink.StartAsync();
        await using var app = new PayLinkApp();
        await SetupAsync(app, new Browser(app), ll);

        var html = await ConnectExistingAsync(new Browser(app), ll, "beta", new("sub-b", "b@beta.in", "B", "end_user", []));
        Assert.Contains("Only an admin of the LoginLink organization", html);
        Assert.Null(await app.Get<PlatformRepository>().TenantBySlugAsync("beta"));
    }

    [Fact]
    public async Task A_new_LoginLink_organization_can_be_created_at_signup()
    {
        await using var ll = await FakeLoginLink.StartAsync();
        await using var app = new PayLinkApp();
        await SetupAsync(app, new Browser(app), ll);

        var b = new Browser(app);
        var r = await b.SubmitAsync("/signup", "CreateNew",
            F(("NewOrgName", "Zen Shop"), ("AdminEmail", "owner@zen.shop"), ("AdminName", "Zen Owner"), ("AdminPassword", "a-strong-password")));
        Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
        ll.NextSignIn("zenshop", ll.OwnerOf("zenshop"));
        r = await b.FollowAsync(r);
        Assert.Contains("Name and address", await b.TextAsync(r));

        r = await b.FollowAsync(await b.SubmitAsync("/signup", "Create",
            F(("TenantName", "Zen Shop"), ("TenantSlug", "zen"), ("DbChoice", "sqlite"), ("action", "create"))));
        Assert.Equal("/zen/console", b.LastUrl!.AbsolutePath);
        var tenant = (await app.Get<PlatformRepository>().TenantBySlugAsync("zen"))!;
        Assert.Equal("created", tenant.LoginlinkMode);
        Assert.Equal("zenshop", tenant.LoginlinkOrg);
    }

    [Fact]
    public async Task Auto_provisioning_is_greyed_out_without_hosting()
    {
        await using var ll = await FakeLoginLink.StartAsync();
        await using var app = new PayLinkApp();
        await SetupAsync(app, new Browser(app), ll);

        var b = new Browser(app);
        var html = await ConnectExistingAsync(b, ll, "gamma", new("sub-g", "g@gamma.in", "G", "tenant_admin", []));
        Assert.Contains("This platform has no database hosting set up.", html.Replace("&#x27;", "'"));

        var r = await b.SubmitAsync("/signup", "Create", F(("TenantName", "Gamma"), ("TenantSlug", "gamma"), ("DbChoice", "auto"), ("action", "create")));
        Assert.Contains("no database hosting", await b.TextAsync(r));
        Assert.Null(await app.Get<PlatformRepository>().TenantBySlugAsync("gamma"));
    }

    [Fact]
    public async Task Platform_admission_waits_for_approval_then_honours_roles()
    {
        await using var ll = await FakeLoginLink.StartAsync();
        await using var app = new PayLinkApp();
        var admin = new Browser(app);
        await SetupAsync(app, admin, ll);

        // Approve-only (default): Amit waits.
        var amit = new FakeLoginLink.Person("sub-amit", "amit@websvc.in", "Amit", "end_user", []);
        var b = new Browser(app);
        ll.NextSignIn("websvc", amit);
        var r = await b.FollowAsync(await b.GetAsync("/admin/login"));
        Assert.Contains("not yet allowed into the platform console", await b.TextAsync(r));
        Assert.Equal(HttpStatusCode.Redirect, (await b.GetAsync("/admin")).StatusCode);

        // Role mode: someone holding paylink_platform_support gets read-only access.
        r = await admin.SubmitAsync("/admin/access", "Mode", F(("mode", "role")));
        Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
        var sana = new FakeLoginLink.Person("sub-sana", "sana@websvc.in", "Sana", "end_user", ["paylink_platform_support"]);
        var s = new Browser(app);
        ll.NextSignIn("websvc", sana);
        r = await s.FollowAsync(await s.GetAsync("/admin/login"));
        Assert.Equal("/admin", s.LastUrl!.AbsolutePath);
        Assert.Contains("support, read-only", await s.TextAsync(r));

        // Read-only users can't change settings.
        r = await s.FollowAsync(await s.SubmitAsync("/admin/settings", null, F(("signup", "off"))));
        Assert.Contains("Your access is read-only", await s.TextAsync(r));
        Assert.True(await app.Get<PlatformSettings>().IsSignupOpenAsync());

        // The last admin can't remove themselves.
        r = await admin.FollowAsync(await admin.SubmitAsync("/admin/access", "Decide", F(("subject", Naren.Subject), ("decision", "remove"))));
        Assert.Contains("can&#x27;t remove or downgrade your own access", await admin.TextAsync(r));
    }

    [Fact]
    public async Task Closed_signup_shows_a_message()
    {
        await using var ll = await FakeLoginLink.StartAsync();
        await using var app = new PayLinkApp();
        var admin = new Browser(app);
        await SetupAsync(app, admin, ll);
        await admin.SubmitAsync("/admin/settings", null, F(("signup", "off"), ("publicBaseUrl", "http://localhost")));

        var r = await new Browser(app).GetAsync("/signup");
        Assert.Contains("Signup is closed", await r.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Admins_choose_which_database_options_tenants_see()
    {
        await using var ll = await FakeLoginLink.StartAsync();
        await using var app = new PayLinkApp();
        var admin = new Browser(app);
        await SetupAsync(app, admin, ll);

        // At least one option must stay on.
        var r = await admin.FollowAsync(await admin.SubmitAsync("/admin/settings", "DbOptions", F()));
        Assert.Contains("Leave at least one option", await admin.TextAsync(r));

        r = await admin.SubmitAsync("/admin/settings", "DbOptions", F(("options", "byo")));
        Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
        Assert.Equal(["byo"], await app.Get<PlatformSettings>().GetTenantDbOptionsAsync());

        // Signup offers only bring-your-own MySQL, and refuses SQLite even when posted directly.
        var priya = new Browser(app);
        var html = await ConnectExistingAsync(priya, ll, "acme", Priya);
        Assert.DoesNotContain("value=\"sqlite\"", html);
        Assert.Contains("value=\"byo\"", html);
        r = await priya.FollowAsync(await priya.SubmitAsync("/signup", "Create",
            F(("TenantName", "Acme Learning"), ("TenantSlug", "acme"), ("DbChoice", "sqlite"), ("action", "create"))));
        Assert.Contains("isn&#x27;t offered on this platform", await priya.TextAsync(r));
        Assert.Null(await app.Get<PlatformRepository>().TenantBySlugAsync("acme"));
    }
}
