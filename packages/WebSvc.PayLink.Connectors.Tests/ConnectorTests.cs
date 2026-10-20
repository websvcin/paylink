using WebSvc.PayLink.Abstractions;
using WebSvc.PayLink.Connector.Simulator;
using WebSvc.PayLink.Connector.Upi;
using Xunit;

namespace WebSvc.PayLink.Connectors.Tests;

public sealed class ConnectorTests
{
    private static AttemptRequest Request(long amount = 149900, string currency = "INR") =>
        new("pay_abc", "att_xyz", amount, currency, "Course & books", new PayerDetails("Rahul", null, null),
            "https://acme.in/done", new Dictionary<string, string>());

    private static ConnectorConfig UpiConfig(PaymentMode mode = PaymentMode.Live, string vpa = "acme@okhdfcbank", string business = "true") =>
        new(mode, new Dictionary<string, string> { ["vpa"] = vpa, ["payee_name"] = "Acme Learning", ["business_vpa_confirmed"] = business }, "");

    [Fact]
    public async Task Upi_shows_a_qr_paying_the_merchant_with_the_payment_id_in_the_note()
    {
        var next = await new UpiConnector().CreateAttemptAsync(Request(), UpiConfig(), default);
        var qr = Assert.IsType<NextAction.ShowQr>(next);
        Assert.Equal("upi://pay?pa=acme%40okhdfcbank&pn=Acme%20Learning&am=1499.00&cu=INR&tr=att_xyz&tn=pay_abc%20Course%20%26%20books", qr.Uri);
        Assert.Equal("att_xyz", qr.GatewayReference);
    }

    [Fact]
    public async Task Upi_refuses_other_currencies_and_a_missing_vpa()
    {
        Assert.IsType<NextAction.Failed>(await new UpiConnector().CreateAttemptAsync(Request(currency: "USD"), UpiConfig(), default));
        Assert.IsType<NextAction.Failed>(await new UpiConnector().CreateAttemptAsync(Request(), UpiConfig(vpa: "nope"), default));
    }

    [Theory]
    [InlineData("acme@okhdfcbank", "true", true)]
    [InlineData("acme", "true", false)]
    [InlineData("acme@okhdfcbank", "false", false)]
    public async Task Upi_settings_are_checked(string vpa, string business, bool ok) =>
        Assert.Equal(ok, (await new UpiConnector().TestConnectionAsync(UpiConfig(vpa: vpa, business: business), default)).Ok);

    [Theory]
    [InlineData("412345678901", true)]
    [InlineData("41234567890", false)]
    [InlineData("41234567890a", false)]
    [InlineData(null, false)]
    public void Utr_is_twelve_digits(string? utr, bool ok) => Assert.Equal(ok, UpiConnector.IsValidUtr(utr));

    [Fact]
    public async Task Simulator_works_in_test_mode_only()
    {
        var sim = new SimulatorConnector();
        var test = new ConnectorConfig(PaymentMode.Test, new Dictionary<string, string>(), "");
        var live = test with { Mode = PaymentMode.Live };
        Assert.Equal(SimulatorConnector.ChooseOutcome, Assert.IsType<NextAction.Sdk>(await sim.CreateAttemptAsync(Request(), test, default)).Payload);
        Assert.IsType<NextAction.Failed>(await sim.CreateAttemptAsync(Request(), live, default));
        Assert.False((await sim.TestConnectionAsync(live, default)).Ok);
        Assert.True(sim.Manifest.TestOnly);
        Assert.Equal(AttemptStatus.Succeeded, SimulatorConnector.Outcome("succeed").Status);
        Assert.Equal(FailureReason.BankDeclined, SimulatorConnector.Outcome("fail").Reason);
    }
}
