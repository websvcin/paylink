using PayLink.Platform;
using PayLink.Tests.Fakes;
using Xunit;
using static PayLink.Tests.Flows;

namespace PayLink.Tests;

/// <summary>Setup's LoginLink step in four sub-steps, each tested before Continue, and the sign-in method PayLink switches on.</summary>
public sealed class SetupLoginLinkTests
{
    private static async Task<Browser> ToLoginLinkStepAsync(PayLinkApp app)
    {
        var b = new Browser(app);
        await b.SubmitAsync("/setup", "Unlock", F(("Code", await app.SetupCodeAsync())));
        await b.SubmitAsync("/setup?step=start", "StartNew", F(("understood", "true")));
        await b.SubmitAsync("/setup?step=hosting", "Hosting", F(("action", "skip")));
        var r = await b.SubmitAsync("/setup?step=database", "Database", F(("DbChoice", "sqlite"), ("action", "save")));
        Assert.Equal("/setup?step=loginlink", r.Headers.Location?.OriginalString);
        return b;
    }

    [Fact]
    public async Task Each_sub_step_is_tested_before_it_continues()
    {
        await using var ll = await FakeLoginLink.StartAsync();
        await using var app = new PayLinkApp();
        var b = await ToLoginLinkStepAsync(app);

        var html = await b.TextAsync(await b.GetAsync("/setup?step=loginlink"));
        Assert.Contains("5.1 The LoginLink server", html);
        Assert.Contains("data-gate=\"untested\"", html);
        Assert.Contains("Test the connection first", await b.TextAsync(await b.SubmitAsync("/setup?step=loginlink&ll=server", "LLServer", F(("action", "continue")))));
        // Later sub-steps can't be skipped to.
        Assert.Contains("5.1 The LoginLink server", await b.TextAsync(await b.GetAsync("/setup?step=loginlink&ll=admins")));

        html = await b.TextAsync(await b.SubmitAsync("/setup?step=loginlink&ll=server", "LLServer",
            F(("action", "test"), ("LL.ServerUrl", ll.BaseUrl), ("LL.PublicBaseUrl", "http://localhost"))));
        Assert.Contains("LoginLink answered", html);
        Assert.Contains("data-gate=\"tested\"", html);
        var r = await b.SubmitAsync("/setup?step=loginlink&ll=server", "LLServer", F(("action", "continue")));
        Assert.Equal("/setup?step=loginlink&ll=platform", r.Headers.Location?.OriginalString);

        // 5.2: an organization's credential isn't a platform one; a real one passes, and its secret isn't asked again.
        var orgCred = ll.AddManagementCredential("websvc");
        html = await b.TextAsync(await b.SubmitAsync("/setup?step=loginlink&ll=platform", "LLPlatform",
            F(("action", "test"), ("LL.PlatformApiClientId", orgCred.ClientId), ("LL.PlatformApiClientSecret", orgCred.Secret))));
        Assert.Contains("organization credential, not a platform one", html);
        var platform = ll.AddPlatformCredential();
        await b.SubmitAsync("/setup?step=loginlink&ll=platform", "LLPlatform",
            F(("action", "test"), ("LL.PlatformApiClientId", platform.ClientId), ("LL.PlatformApiClientSecret", platform.Secret)));
        html = await b.TextAsync(await b.SubmitAsync("/setup?step=loginlink&ll=platform", "LLPlatform",
            F(("action", "test"), ("LL.PlatformApiClientId", platform.ClientId), ("LL.PlatformApiClientSecret", ""))));
        Assert.Contains("The platform credential works", html);
        Assert.Contains("••••", html);
        Assert.DoesNotContain(platform.Secret, html);
        await b.SubmitAsync("/setup?step=loginlink&ll=platform", "LLPlatform", F(("action", "continue")));

        // 5.3: a credential of another organization is caught; the right one passes.
        html = await b.TextAsync(await b.SubmitAsync("/setup?step=loginlink&ll=org", "LLOrg",
            F(("action", "test"), ("LL.AppMode", "register"), ("LL.PlatformOrg", "acme"), ("LL.MgmtClientId", orgCred.ClientId), ("LL.MgmtClientSecret", orgCred.Secret))));
        Assert.Contains("belongs to the organization \"websvc\"", System.Net.WebUtility.HtmlDecode(html));
        html = await b.TextAsync(await b.SubmitAsync("/setup?step=loginlink&ll=org", "LLOrg",
            F(("action", "test"), ("LL.AppMode", "register"), ("LL.PlatformOrg", "websvc"), ("LL.MgmtClientId", orgCred.ClientId), ("LL.MgmtClientSecret", orgCred.Secret))));
        Assert.Contains("The management credential works", html);
        r = await b.SubmitAsync("/setup?step=loginlink&ll=org", "LLOrg", F(("action", "continue")));
        Assert.Equal("/setup?step=loginlink&ll=admins", r.Headers.Location?.OriginalString);
        Assert.Equal(0, ll.AppConnectors.Count);   // nothing created in LoginLink yet

        // 5.4: finish. The app is registered with a sign-in method on, and the /admin rule is saved.
        html = await b.TextAsync(await b.GetAsync("/setup?step=loginlink&ll=admins"));
        Assert.Contains("Only people we approve", html);
        r = await b.SubmitAsync("/setup?step=loginlink&ll=admins", "LLAdmins", F(("AdminAdmission", "role")));
        Assert.Equal("/setup?step=admin", r.Headers.Location?.OriginalString);
        Assert.Contains(ll.AppConnectors.Values, set => set.Contains("Password"));
        Assert.Equal(AdmissionModes.Role, await app.Get<PlatformSettings>().GetAdminAdmissionAsync());
        var saved = (await app.Get<PlatformSettings>().GetLoginLinkAsync())!;
        Assert.Equal(("websvc", platform.ClientId), (saved.PlatformOrg, saved.PlatformApiClientId));

        ll.NextSignIn("websvc", Naren);
        html = await b.TextAsync(await b.FollowAsync(await b.SubmitAsync("/setup?step=admin", "SignIn", F())));
        Assert.Contains("Setup is complete", html);
    }

    [Fact]
    public async Task Without_Manage_connectors_setup_says_what_to_turn_on()
    {
        await using var ll = await FakeLoginLink.StartAsync();
        ll.ManageConnectorsAllowed = false;
        await using var app = new PayLinkApp();
        var b = await ToLoginLinkStepAsync(app);
        var mgmt = ll.AddManagementCredential("websvc");
        var r = await b.SubmitAsync("/setup?step=loginlink", "LoginLink", F(("LL.ServerUrl", ll.BaseUrl), ("LL.PlatformOrg", "websvc"),
            ("LL.PublicBaseUrl", "http://localhost"), ("LL.AppMode", "register"), ("LL.MgmtClientId", mgmt.ClientId), ("LL.MgmtClientSecret", mgmt.Secret)));
        var html = await b.TextAsync(await b.FollowAsync(r));
        Assert.Contains("Turn on a sign-in method", html);
        Assert.Contains("Connectors", html);
    }
}
