using PayLink.Tests.Fakes;
using Xunit;

namespace PayLink.Tests;

/// <summary>Reusable steps: run first-run setup and sign people in, the way a browser would.</summary>
public static class Flows
{
    public static readonly FakeLoginLink.Person Naren = new("sub-naren", "naren@websvc.in", "Narender K", "tenant_admin", []);

    public static Dictionary<string, string?> F(params (string Key, string? Value)[] pairs) => pairs.ToDictionary(p => p.Key, p => p.Value);

    /// <summary>Runs /setup to completion and leaves <paramref name="browser"/> signed in to /admin as Naren.</summary>
    /// <summary>Runs setup; the platform database is SQLite, or the server database the suite runs on, unless <paramref name="database"/> says otherwise.</summary>
    public static async Task SetupAsync(PayLinkApp app, Browser browser, FakeLoginLink ll,
        Dictionary<string, string?>? hosting = null, Dictionary<string, string?>? database = null, bool withPlatformApi = true)
    {
        var r = await browser.SubmitAsync("/setup", "Unlock", F(("Code", await app.SetupCodeAsync())));
        Assert.Equal("/setup?step=start", r.Headers.Location?.OriginalString);

        r = await browser.SubmitAsync("/setup?step=start", "StartNew", F(("keyPlace", "bootstrap"), ("understood", "true")));
        Assert.Equal("/setup?step=hosting", r.Headers.Location?.OriginalString);

        r = await browser.SubmitAsync("/setup?step=hosting", "Hosting", hosting ?? F(("action", "skip")));
        Assert.Equal("/setup?step=database", r.Headers.Location?.OriginalString);

        database ??= ServerDbTest.On
            ? F(("DbChoice", "mysql"), ("Mysql.Engine", ServerDbTest.Engine), ("Mysql.UseConnectionString", "true"), ("Mysql.ConnectionString", ServerDbTest.NewDatabase()), ("action", "save"))
            : F(("DbChoice", "sqlite"), ("action", "save"));
        r = await browser.SubmitAsync("/setup?step=database", "Database", database);
        Assert.True(r.Headers.Location?.OriginalString == "/setup?step=loginlink", await Problem(browser, r));

        var mgmt = ll.AddManagementCredential("websvc");
        var fields = F(("LL.ServerUrl", ll.BaseUrl), ("LL.PlatformOrg", "websvc"), ("LL.PublicBaseUrl", "http://localhost"),
            ("LL.AppMode", "register"), ("LL.MgmtClientId", mgmt.ClientId), ("LL.MgmtClientSecret", mgmt.Secret));
        if (withPlatformApi)
        {
            var platform = ll.AddPlatformCredential();
            fields["LL.PlatformApiClientId"] = platform.ClientId;
            fields["LL.PlatformApiClientSecret"] = platform.Secret;
        }
        r = await browser.SubmitAsync("/setup?step=loginlink", "LoginLink", fields);
        Assert.True(r.Headers.Location?.OriginalString == "/setup?step=admin", await Problem(browser, r));

        ll.NextSignIn("websvc", Naren);
        r = await browser.FollowAsync(await browser.SubmitAsync("/setup?step=admin", "SignIn", F()));
        var html = await browser.TextAsync(r);
        Assert.Contains("Setup is complete", html);
        Assert.Equal("/admin", browser.LastUrl!.AbsolutePath);
    }

    /// <summary>Signs a person in to a tenant console through its LoginLink organization and returns the final page.</summary>
    public static async Task<(HttpResponseMessage Response, string Html)> ConsoleSignInAsync(Browser browser, FakeLoginLink ll, string slug, string org, FakeLoginLink.Person person)
    {
        ll.NextSignIn(org, person);
        var r = await browser.FollowAsync(await browser.GetAsync($"/{slug}/login?go=true"));
        return (r, await browser.TextAsync(r));
    }

    /// <summary>Starts signup by connecting an existing organization, signs in as <paramref name="person"/>, and returns the page.</summary>
    public static async Task<string> ConnectExistingAsync(Browser browser, FakeLoginLink ll, string org, FakeLoginLink.Person person)
    {
        var cred = ll.AddManagementCredential(org);
        ll.NextSignIn(org, person);
        var r = await browser.FollowAsync(await browser.SubmitAsync("/signup", "ConnectExisting",
            F(("OrgSlug", org), ("MgmtClientId", cred.ClientId), ("MgmtClientSecret", cred.Secret))));
        return await browser.TextAsync(r);
    }

    /// <summary>
    /// Connects <paramref name="org"/> and creates a tenant; returns the owner's signed-in console browser. The default database is
    /// SQLite, or the server database the suite runs on (<see cref="ServerDbTest"/>); pass "sqlite" to insist on SQLite.
    /// </summary>
    public static readonly Dictionary<string, string?> SqlitePlatform = F(("DbChoice", "sqlite"), ("action", "save"));

    public static async Task<Browser> CreateTenantAsync(PayLinkApp app, FakeLoginLink ll, string slug, string name, FakeLoginLink.Person owner, string db = "default")
    {
        var b = new Browser(app);
        await ConnectExistingAsync(b, ll, slug, owner);
        var fields = F(("TenantName", name), ("TenantSlug", slug), ("DbChoice", db == "default" ? "sqlite" : db), ("action", "create"));
        if (db == "default" && ServerDbTest.On)
        {
            fields["DbChoice"] = "byo";
            fields["Byo.Engine"] = ServerDbTest.Engine;
            fields["Byo.UseConnectionString"] = "true";
            fields["Byo.ConnectionString"] = ServerDbTest.NewDatabase();
        }
        var r = await b.FollowAsync(await b.SubmitAsync("/signup", "Create", fields));
        Assert.True(b.LastUrl!.AbsolutePath == $"/{slug}/console", await Problem(b, r));
        return b;
    }

    /// <summary>Creates a key on the Developers page and returns the secret, which is shown once.</summary>
    public static async Task<string> CreateKeyAsync(Browser b, string slug, string kind = "secret", bool live = false, params string[] capabilities)
    {
        await b.GetAsync($"/{slug}/console?mode={(live ? "live" : "test")}");
        var fields = F(("name", kind == "secret" ? "Shop server" : "Automation"), ("kind", kind));
        var r = await b.FollowAsync(await b.SubmitAsync($"/{slug}/console/developers", "Key",
            capabilities.Length == 0 ? fields : fields.Concat(capabilities.Select((c, i) => KeyValuePair.Create($"capabilities[{i}]", (string?)c))).ToDictionary()));
        var html = await b.TextAsync(r);
        var m = System.Text.RegularExpressions.Regex.Match(html, kind == "secret" ? @"sk_(test|live)_[a-z0-9-]+_[A-Za-z0-9]{32}" : @"tdk_[a-z0-9-]+_[A-Za-z0-9]{32}");
        Assert.True(m.Success, "No key on the page: " + await Problem(b, r));
        return m.Value;
    }

    public static async Task<string> Problem(Browser browser, HttpResponseMessage r)
    {
        var html = await browser.TextAsync(r);
        var m = System.Text.RegularExpressions.Regex.Match(html, "class=\"notice bad\"[^>]*>([^<]+)<");
        return $"{(int)r.StatusCode} {r.Headers.Location} {(m.Success ? m.Groups[1].Value : "")}";
    }
}
