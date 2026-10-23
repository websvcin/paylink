using System.Collections.Concurrent;
using System.Net;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using PayLink.Payments;
using PayLink.Platform;
using PayLink.Tests.Fakes;
using Xunit;
using static PayLink.Tests.Flows;

namespace PayLink.Tests;

/// <summary>Phase 4: file storage in the data folder or FileLink (the FileLink contract in the docs), and switching between them.</summary>
public sealed class FileStorageTests
{
    private static readonly FakeLoginLink.Person Priya = new("sub-priya", "priya@acme.in", "Priya", "tenant_admin", []);
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 0x0D, 1, 2, 3];

    private sealed class FakeFileLink : HttpMessageHandler
    {
        public ConcurrentDictionary<string, (byte[] Bytes, string Type, string Path)> Files { get; } = new();
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.Headers.Authorization?.Parameter != "fl_key") return new HttpResponseMessage(HttpStatusCode.Unauthorized);
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/api/v1/health") return new HttpResponseMessage(HttpStatusCode.OK);
            if (request.Method == HttpMethod.Put && path.StartsWith("/api/v1/files/"))
            {
                var id = "f_" + Files.Count;
                Files[id] = (await request.Content!.ReadAsByteArrayAsync(ct), request.Content.Headers.ContentType!.MediaType!, Uri.UnescapeDataString(path["/api/v1/files/".Length..]));
                return new HttpResponseMessage(HttpStatusCode.Created) { Content = new StringContent($$"""{"id":"{{id}}"}""", Encoding.UTF8, "application/json") };
            }
            if (request.Method == HttpMethod.Get && Files.TryGetValue(path["/api/v1/files/".Length..], out var f))
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(f.Bytes) { Headers = { ContentType = new(f.Type) } } };
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }
    }

    private static async Task<(string PaymentId, string AttemptId)> PayByUpiWithScreenshot(PayLinkApp app, TenantRecord t, string utr)
    {
        var svc = app.Get<PaymentService>();
        var p = (await svc.CreateAsync(t, new CreatePaymentRequest(1000, null, null, null, null, null, null, null, null, null, null), false, null, EventSource.System, "api")).Payment!;
        var payer = new Browser(app);
        await payer.SubmitAsync($"/pay/{p.Id}", "Choose", F(("connector", "upi")));
        var a = (await svc.AttemptsAsync(t, p.Id)).Single();
        await payer.SubmitWithFileAsync($"/pay/{p.Id}", "Utr", F(("attemptId", a.Id), ("utr", utr)), "screenshot", "paid.png", Png, "image/png");
        return (p.Id, a.Id);
    }

    [Fact]
    public async Task Screenshots_go_to_FileLink_and_stay_readable_after_switching()
    {
        var fileLink = new FakeFileLink();
        await using var ll = await FakeLoginLink.StartAsync();
        await using var app = new PayLinkApp(services: s => s.AddHttpClient(FileLinkBackend.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => fileLink));
        var admin = new Browser(app);
        await SetupAsync(app, admin, ll);
        var priya = await CreateTenantAsync(app, ll, "acme", "Acme Learning", Priya);
        var tenant = (await app.Get<PlatformRepository>().TenantBySlugAsync("acme"))!;
        await priya.SubmitAsync("/acme/console/methods", null,
            F(("connector", "upi"), ("mode", "test"), ("action", "enable"), ("test.vpa", "acme@okhdfcbank"), ("test.payee_name", "Acme Learning"), ("test.business_vpa_confirmed", "true")));

        // A local screenshot first.
        var (localPay, localAttempt) = await PayByUpiWithScreenshot(app, tenant, "411111111111");
        var localRef = (await app.Get<PaymentService>().AttemptsAsync(tenant, localPay)).Single().ProofFile!;
        Assert.DoesNotContain("filelink:", localRef);

        // FileLink with a wrong key isn't saved; the right one is.
        var r = await admin.FollowAsync(await admin.SubmitAsync("/admin/settings", "Files", F(("kind", "filelink"), ("server", "https://files.example.in"), ("key", "nope"))));
        Assert.Contains("the key wasn&#x27;t accepted", await admin.TextAsync(r));
        r = await admin.FollowAsync(await admin.SubmitAsync("/admin/settings", "Files", F(("kind", "filelink"), ("server", "https://files.example.in/"), ("key", "fl_key"))));
        Assert.Contains("FileLink answered", await admin.TextAsync(r));

        var (flPay, flAttempt) = await PayByUpiWithScreenshot(app, tenant, "422222222222");
        var flRef = (await app.Get<PaymentService>().AttemptsAsync(tenant, flPay)).Single().ProofFile!;
        Assert.Equal("filelink:f_0", flRef);
        Assert.Equal(Png, fileLink.Files["f_0"].Bytes);
        Assert.StartsWith($"{tenant.Id}/", fileLink.Files["f_0"].Path);

        // Both open for a signed-in member, from wherever they are; and after switching back.
        foreach (var (pay, att) in new[] { (localPay, localAttempt), (flPay, flAttempt) })
        {
            var proof = await priya.GetAsync($"/acme/console/payments/{pay}?handler=Proof&attemptId={att}");
            Assert.Equal("image/png", proof.Content.Headers.ContentType?.MediaType);
            Assert.Equal(Png, await proof.Content.ReadAsByteArrayAsync());
        }
        await admin.SubmitAsync("/admin/settings", "Files", F(("kind", "local")));
        var again = await priya.GetAsync($"/acme/console/payments/{localPay}?handler=Proof&attemptId={localAttempt}");
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Assert.Equal(Png, await (await priya.GetAsync($"/acme/console/payments/{flPay}?handler=Proof&attemptId={flAttempt}")).Content.ReadAsByteArrayAsync());
        // Someone not signed in is sent to sign in, never the file.
        var stranger = await new Browser(app).GetAsync($"/acme/console/payments/{flPay}?handler=Proof&attemptId={flAttempt}");
        Assert.Equal(HttpStatusCode.Redirect, stranger.StatusCode);
    }
}
