using PayLink.Platform;
using PayLink.Tests.Fakes;
using Xunit;
using static PayLink.Tests.Flows;

namespace PayLink.Tests;

/// <summary>Round 5: partner onboarding. The platform offers help getting a gateway account; tenants' requests are kept per tenant.</summary>
public sealed class PartnerOnboardingTests
{
    private static readonly FakeLoginLink.Person Priya = new("sub-priya", "priya@acme.in", "Priya", "tenant_admin", []);

    [Fact]
    public async Task A_partner_link_records_interest_and_a_form_reaches_the_platform_team()
    {
        await using var ll = await FakeLoginLink.StartAsync();
        await using var app = new PayLinkApp();
        var admin = new Browser(app);
        await SetupAsync(app, admin, ll);
        var priya = await CreateTenantAsync(app, ll, "acme", "Acme Learning", Priya);

        // Nothing configured: tenants only see their own-account path.
        var html = await priya.TextAsync(await priya.GetAsync("/acme/console/methods"));
        Assert.Contains("I have an account", html);
        Assert.DoesNotContain("Get an account", html);

        // A link must be https.
        var r = await admin.FollowAsync(await admin.SubmitAsync("/admin/partners", "Program", F(("connector", "payu"), ("mode", "link"), ("link", "http://partner.example/x"))));
        Assert.Contains("full https:// address", await admin.TextAsync(r));
        await admin.SubmitAsync("/admin/partners", "Program", F(("connector", "payu"), ("mode", "link"), ("link", "https://partner.example/signup?ref=websvc&m={tenant}"), ("headline", "Get PayU in 2 minutes")));
        await admin.SubmitAsync("/admin/partners", "Program", F(("connector", "razorpay"), ("mode", "form")));

        html = await priya.TextAsync(await priya.GetAsync("/acme/console/methods"));
        Assert.Contains("Get PayU in 2 minutes", html);
        Assert.Contains("Get a Razorpay account", html);

        r = await priya.SubmitAsync("/acme/console/methods", "Partner", F(("connector", "payu")));
        Assert.Equal("https://partner.example/signup?ref=websvc&m=acme", r.Headers.Location?.OriginalString);

        r = await priya.FollowAsync(await priya.SubmitAsync("/acme/console/methods", "Partner", F(("connector", "razorpay"), ("name", "Priya"), ("email", "priya@acme.in"), ("phone", "+91 98123 45678"),
            ("q_business", "Acme Learning Pvt Ltd"), ("q_volume", "5,00,000"))));
        Assert.Contains("Request sent for Razorpay", await priya.TextAsync(r));
        // Asking again updates the open request instead of adding another.
        await priya.SubmitAsync("/acme/console/methods", "Partner", F(("connector", "razorpay"), ("name", "Priya"), ("email", "priya@acme.in"), ("phone", "+91 98123 45678")));

        var requests = await app.Get<PartnerOnboarding>().ListAsync(null, null);
        Assert.Equal(2, requests.Count);
        var form = requests.Single(x => x.ConnectorId == "razorpay");
        Assert.Contains(form.Answers, a => a.Value == "Acme Learning Pvt Ltd");

        html = await admin.TextAsync(await admin.GetAsync("/admin/partners?status=new"));
        Assert.Contains("Acme Learning Pvt Ltd", html);
        Assert.Contains("opened partner link", html);
        Assert.Contains("Account requests", await admin.TextAsync(await admin.GetAsync("/admin/tenants/acme")));

        await admin.SubmitAsync("/admin/partners", "Request", F(("id", form.Id.ToString()), ("status", "done"), ("note", "Approved; keys emailed")));
        html = await priya.TextAsync(await priya.GetAsync("/acme/console/methods"));
        Assert.Contains("Account: Account ready", html);
        Assert.Contains("Approved; keys emailed", html);
    }
}
