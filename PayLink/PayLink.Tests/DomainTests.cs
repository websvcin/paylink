using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using PayLink.Tenancy;
using PayLink.Tests.Fakes;
using Xunit;
using static PayLink.Tests.Flows;

namespace PayLink.Tests;

/// <summary>Phase 4: a tenant's own domain, proven with a TXT record, then serving its console, links and checkout.</summary>
public sealed class DomainTests
{
    private static readonly FakeLoginLink.Person Priya = new("sub-priya", "priya@acme.in", "Priya", "tenant_admin", []);

    private sealed class FakeDns : IDnsTxtResolver
    {
        public Dictionary<string, string[]> Records { get; } = [];
        public Task<IReadOnlyList<string>> TxtAsync(string name, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<string>>(Records.GetValueOrDefault(name, []));
    }

    [Fact]
    public async Task A_verified_domain_serves_the_console_links_and_checkout()
    {
        var dns = new FakeDns();
        await using var ll = await FakeLoginLink.StartAsync();
        await using var app = new PayLinkApp(services: s => s.AddSingleton<IDnsTxtResolver>(dns));
        var admin = new Browser(app);
        await SetupAsync(app, admin, ll);
        var priya = await CreateTenantAsync(app, ll, "acme", "Acme Learning", Priya);

        var r = await priya.FollowAsync(await priya.SubmitAsync("/acme/console/domain", "Add", F(("domain", "not a domain"))));
        Assert.Contains("Enter a domain name", await priya.TextAsync(r));
        r = await priya.FollowAsync(await priya.SubmitAsync("/acme/console/domain", "Add", F(("domain", "https://Pay.Acme.in/"))));
        var html = await priya.TextAsync(r);
        Assert.Contains("_paylink-challenge.pay.acme.in", html);
        var d = (await app.Get<CustomDomains>().ForTenantAsync((await app.Get<PayLink.Platform.PlatformRepository>().TenantBySlugAsync("acme"))!.Id))!;
        Assert.Equal("pay.acme.in", d.Domain);

        // Nothing in DNS yet: still pending, and the host isn't served.
        r = await priya.FollowAsync(await priya.SubmitAsync("/acme/console/domain", "Verify", F()));
        Assert.Contains("No TXT record at _paylink-challenge.pay.acme.in yet", await priya.TextAsync(r));
        var onDomain = app.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, BaseAddress = new Uri("https://pay.acme.in") });
        Assert.NotEqual("/acme/login", (await onDomain.GetAsync("/")).Headers.Location?.OriginalString);
        Assert.Equal(System.Net.HttpStatusCode.NotFound, (await onDomain.GetAsync("/.well-known/paylink/domain?domain=pay.acme.in")).StatusCode);

        dns.Records[d.ChallengeName] = ["something else", d.ChallengeValue];
        r = await priya.FollowAsync(await priya.SubmitAsync("/acme/console/domain", "Verify", F()));
        Assert.Contains("pay.acme.in is verified", await priya.TextAsync(r));
        Assert.Contains(ll.AppPatches.Values, p => p["redirectUris"] is JsonArray uris && uris.Any(u => (string?)u == "https://pay.acme.in/auth/callback"));

        // The domain serves the console (sign-in returns there), the proxy check says yes, and payers get the domain in URLs.
        Assert.Equal("/acme/login", (await onDomain.GetAsync("/")).Headers.Location?.OriginalString);
        Assert.Contains("redirect_uri=https%3A%2F%2Fpay.acme.in%2Fauth%2Fcallback", (await onDomain.GetAsync("/login?go=true")).Headers.Location!.ToString());
        Assert.Equal(System.Net.HttpStatusCode.OK, (await onDomain.GetAsync("/.well-known/paylink/domain?domain=pay.acme.in")).StatusCode);
        var api = new Browser(app).Api;
        api.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", await CreateKeyAsync(priya, "acme"));
        var payment = (await (await api.PostAsJsonAsync("/api/v1/payments", new { amount = 1000 })).Content.ReadFromJsonAsync<JsonObject>())!;
        Assert.StartsWith("https://pay.acme.in/pay/pay_", (string)payment["checkout_url"]!);
        Assert.Contains("Acme Learning", await (await onDomain.GetAsync(new Uri((string)payment["checkout_url"]!).PathAndQuery)).Content.ReadAsStringAsync());

        // Another tenant can't take the name; removing it goes back to the platform address.
        var zeta = await CreateTenantAsync(app, ll, "zeta2", "Zeta Two", new("sub-z2", "z2@zeta.in", "Zed2", "tenant_admin", []));
        r = await zeta.FollowAsync(await zeta.SubmitAsync("/zeta2/console/domain", "Add", F(("domain", "pay.acme.in"))));
        Assert.Contains("already in use", await zeta.TextAsync(r));
        await priya.SubmitAsync("/acme/console/domain", "Remove", F());
        payment = (await (await api.PostAsJsonAsync("/api/v1/payments", new { amount = 1000 })).Content.ReadFromJsonAsync<JsonObject>())!;
        Assert.StartsWith("http://localhost/pay/", (string)payment["checkout_url"]!);
        Assert.NotEqual("/acme/login", (await onDomain.GetAsync("/")).Headers.Location?.OriginalString);
    }
}
