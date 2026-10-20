using System.Text;
using PayLink.Payments;
using PayLink.Platform;
using PayLink.Tests.Fakes;
using Xunit;
using static PayLink.Tests.Flows;

namespace PayLink.Tests;

/// <summary>Phase 4: routing (order, amount ranges, circuit breaker, failover) and UPI bank statement matching.</summary>
public sealed class RoutingTests
{
    private static readonly FakeLoginLink.Person Priya = new("sub-priya", "priya@acme.in", "Priya", "tenant_admin", []);

    private static Task<PaymentResult> Pay(PayLinkApp app, TenantRecord t, long amount) =>
        app.Get<PaymentService>().CreateAsync(t, new CreatePaymentRequest(amount, null, null, "Fees", null, "a@b.in", null, null, null, null, null), false, null, EventSource.System, "api");

    private static async Task<IReadOnlyList<string>> Offered(PayLinkApp app, TenantRecord t, Payment p) =>
        (await app.Get<ConnectorSettingsService>().ForCheckoutAsync(t, p)).Select(c => c.Manifest.Id).ToList();

    [Fact]
    public async Task Methods_follow_the_order_and_amounts_and_failing_gateways_hand_over()
    {
        var rzp = new FakeRazorpay();
        await using var ll = await FakeLoginLink.StartAsync();
        await using var app = new PayLinkApp(gateways: rzp);
        await SetupAsync(app, new Browser(app), ll);
        var priya = await CreateTenantAsync(app, ll, "acme", "Acme Learning", Priya);
        var tenant = (await app.Get<PlatformRepository>().TenantBySlugAsync("acme"))!;
        await priya.SubmitAsync("/acme/console/methods", null, F(("connector", "razorpay"), ("mode", "test"), ("action", "enable"), ("test.key_id", "rzp_test_x"), ("test.key_secret", "sec")));
        await priya.SubmitAsync("/acme/console/methods", null, F(("connector", "payu"), ("mode", "test"), ("action", "enable"), ("test.merchant_key", "payukey"), ("test.merchant_salt", "salt")));

        // Simulator first, Razorpay, then PayU; the Simulator only up to ₹1,000.
        var r = await priya.FollowAsync(await priya.SubmitAsync("/acme/console/methods", "Routing", F(
            ("order_simulator", "0"), ("max_simulator", "1,000"), ("order_razorpay", "1"), ("order_payu", "2"), ("failover", "off"))));
        Assert.Contains("Routing saved for test.", await priya.TextAsync(r));
        var small = (await Pay(app, tenant, 50000)).Payment!;
        var large = (await Pay(app, tenant, 500000)).Payment!;
        Assert.Equal(["simulator", "razorpay", "payu"], await Offered(app, tenant, small));
        Assert.Equal(["razorpay", "payu"], await Offered(app, tenant, large));

        // Razorpay is down. With failover off, the payer is told and chooses again.
        rzp.Down = true;
        var payer = new Browser(app);
        r = await payer.FollowAsync(await payer.SubmitAsync($"/pay/{large.Id}", "Choose", F(("connector", "razorpay"))));
        Assert.Contains("Razorpay couldn&#x27;t start", await payer.TextAsync(r));
        Assert.Single(await app.Get<PaymentService>().AttemptsAsync(tenant, large.Id));

        // With failover on, the payer goes straight on to PayU.
        await priya.SubmitAsync("/acme/console/methods", "Routing", F(("order_simulator", "0"), ("max_simulator", "1,000"), ("order_razorpay", "1"), ("order_payu", "2"), ("failover", "on")));
        Assert.True(await app.Get<ConnectorSettingsService>().FailoverOnAsync(tenant));
        r = await payer.FollowAsync(await payer.SubmitAsync($"/pay/{large.Id}", "Choose", F(("connector", "razorpay"))));
        Assert.Contains("Continue to PayU", await payer.TextAsync(r));
        var attempts = await app.Get<PaymentService>().AttemptsAsync(tenant, large.Id);
        Assert.Equal([("razorpay", "failed"), ("razorpay", "failed"), ("payu", "requires_action")], attempts.Select(a => (a.Connector, a.Status)).ToList());
        var detail = await app.Get<PaymentService>().DetailAsync(tenant, large.Id);
        Assert.Contains(detail!.Value.Events, e => e.Type == "attempt.failover");

        // A third error in a row: Razorpay rests, and checkout leaves it out.
        var third = (await Pay(app, tenant, 300000)).Payment!;
        Assert.Equal(["razorpay", "payu"], await Offered(app, tenant, third));
        await new Browser(app).SubmitAsync($"/pay/{third.Id}", "Choose", F(("connector", "razorpay")));
        Assert.Equal(["payu"], await Offered(app, tenant, third));
        Assert.Contains("Resting after errors", await priya.TextAsync(await priya.GetAsync("/acme/console/methods")));
        Assert.Contains("PayLink moved the payer to payu", await priya.TextAsync(await priya.GetAsync($"/acme/console/payments/{third.Id}")));
    }

    private const string Hdfc = """
        HDFC BANK Ltd.,,,,,,
        Statement of account,Acme Learning,,,,,
        ,,,,,,
        Date,Narration,Chq./Ref.No.,Value Dt,Withdrawal Amt.,Deposit Amt.,Closing Balance
        20/10/26,UPI-RAVI-ravi@okhdfc-HDFC0001-411111111111-Fees,0000411111111111,20/10/26,,"1,499.00","10,000.00"
        20/10/26,UPI-ASHA-asha@ybl-YESB0001-422222222222-Fees,0000422222222222,20/10/26,,450.00,"10,450.00"
        20/10/26,UPI-KIRAN-kiran@ibl-499999999999-Rent share,0000499999999999,20/10/26,,75.00,"10,525.00"
        20/10/26,NEFT-RENT,N1234,20/10/26,"5,000.00",,"5,525.00"
        """;

    [Fact]
    public void Statements_read_the_credits_and_the_UTR()
    {
        var (lines, problem) = UpiStatementService.Read(Hdfc);
        Assert.Null(problem);
        Assert.Equal([("411111111111", 149900L), ("422222222222", 45000L), ("499999999999", 7500L)], lines.Select(l => (l.Utr!, l.Amount)).ToList());

        // ICICI style: one amount column and a Cr/Dr column.
        (lines, problem) = UpiStatementService.Read("Transaction Date,Transaction Remarks,Amount (INR),Cr/Dr\n21-10-2026,UPI/433333333333/Fees,250.00,CR\n21-10-2026,ATM WDL,2000.00,DR\n");
        Assert.Equal(("433333333333", 25000L), (lines.Single().Utr!, lines.Single().Amount));
        Assert.NotNull(UpiStatementService.Read("just,some\ntext,here").Problem);
    }

    [Fact]
    public async Task A_bank_statement_approves_matching_UPI_payments()
    {
        await using var ll = await FakeLoginLink.StartAsync();
        await using var app = new PayLinkApp();
        await SetupAsync(app, new Browser(app), ll);
        var priya = await CreateTenantAsync(app, ll, "acme", "Acme Learning", Priya);
        var tenant = (await app.Get<PlatformRepository>().TenantBySlugAsync("acme"))!;
        await priya.SubmitAsync("/acme/console/methods", null,
            F(("connector", "upi"), ("mode", "test"), ("action", "enable"), ("test.vpa", "acme@okhdfcbank"), ("test.payee_name", "Acme Learning"), ("test.business_vpa_confirmed", "true")));
        var svc = app.Get<PaymentService>();
        var ids = new List<string>();
        foreach (var (amount, utr) in new[] { (149900L, "411111111111"), (50000L, "422222222222"), (20000L, "433333333333") })
        {
            var p = (await Pay(app, tenant, amount)).Payment!;
            var payer = new Browser(app);
            await payer.SubmitAsync($"/pay/{p.Id}", "Choose", F(("connector", "upi")));
            var a = (await svc.AttemptsAsync(tenant, p.Id)).Single();
            await payer.SubmitAsync($"/pay/{p.Id}", "Utr", F(("attemptId", a.Id), ("utr", utr)));
            ids.Add(p.Id);
        }

        var r = await priya.FollowAsync(await priya.SubmitWithFileAsync("/acme/console/upi", "Statement", F(), "statement", "hdfc.csv", Encoding.UTF8.GetBytes(Hdfc), "text/csv"));
        var html = await priya.TextAsync(r);
        Assert.Contains("3 credits read: 1 UPI payment approved, 1 need a look, 1 not PayLink payments.", html);
        Assert.Contains("PayLink expects ₹500.00, the bank shows ₹450.00.", html);
        Assert.Equal([PaymentStatuses.Succeeded, PaymentStatuses.Processing, PaymentStatuses.Processing],
            (await Task.WhenAll(ids.Select(id => svc.GetAsync(tenant, id)))).Select(p => p!.Status).ToList());
        var journey = await svc.DetailAsync(tenant, ids[0]);
        Assert.Contains(journey!.Value.Events, e => e.Type == "upi.approved" && e.Data!.Contains("Bank statement: UTR 411111111111"));

        // The same statement again approves nothing twice.
        r = await priya.FollowAsync(await priya.SubmitWithFileAsync("/acme/console/upi", "Statement", F(), "statement", "hdfc.csv", Encoding.UTF8.GetBytes(Hdfc), "text/csv"));
        Assert.Contains("0 UPI payments approved", await priya.TextAsync(r));
    }
}
