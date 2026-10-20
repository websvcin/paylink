using System.Net;
using PayLink.Payments;
using WebSvc.PayLink.Abstractions;
using Xunit;

namespace PayLink.Tests;

public sealed class PaymentUnitTests
{
    [Theory]
    [InlineData("created", "requires_action", false, true)]
    [InlineData("requires_action", "requires_action", false, true)]   // a new attempt
    [InlineData("processing", "requires_action", false, false)]
    [InlineData("processing", "requires_action", true, true)]         // the attempt failed: try another method
    [InlineData("succeeded", "failed", false, false)]                 // final never changes
    [InlineData("failed", "succeeded", false, false)]
    [InlineData("on_hold", "created", false, true)]
    [InlineData("on_hold", "requires_action", false, false)]
    [InlineData("processing", "succeeded", false, true)]
    [InlineData("created", "processing", false, true)]
    public void Payment_statuses_only_move_forward(string from, string to, bool retry, bool allowed) =>
        Assert.Equal(allowed, PaymentStatuses.CanMove(from, to, retry));

    [Theory]
    [InlineData(149900, "INR", "₹1,499.00")]
    [InlineData(1234567850, "INR", "₹1,23,45,678.50")]
    [InlineData(5, "INR", "₹0.05")]
    [InlineData(10000000, "INR", "₹1,00,000.00")]
    [InlineData(250000, "USD", "$2,500.00")]
    public void Money_is_formatted_for_people(long minor, string currency, string expected) => Assert.Equal(expected, Money.Format(minor, currency));

    [Theory]
    [InlineData("1,499.50", 149950L)]
    [InlineData("₹ 10", 1000L)]
    [InlineData("0", null)]
    [InlineData("abc", null)]
    public void Typed_amounts_become_paise(string text, long? expected) => Assert.Equal(expected, Money.ParseMajor(text));

    [Fact]
    public void Risk_rules_block_before_review()
    {
        var rules = new[]
        {
            new RiskRule { Kind = "amount_above", Value = "1000", Action = RiskActions.Review },
            new RiskRule { Kind = "email_domain", Value = "@mailinator.com", Action = RiskActions.Block },
            new RiskRule { Kind = "phone", Value = "98123 45678", Action = RiskActions.Block, Enabled = false },
        };
        Assert.True(RiskEngine.OnCreate(rules, new Payment { Amount = 200000, CustomerEmail = "x@MAILINATOR.com" }).Blocked);
        Assert.True(RiskEngine.OnCreate(rules, new Payment { Amount = 200000, CustomerEmail = "x@acme.in" }).Review);
        Assert.Equal("allow", RiskEngine.OnCreate(rules, new Payment { Amount = 100000, CustomerPhone = "+91 98123 45678" }).Action);
        Assert.Null(RiskEngine.Problem("ip", "203.0.113.0/24", "block"));
        Assert.NotNull(RiskEngine.Problem("ip", "203.0.113", "block"));
        Assert.NotNull(RiskEngine.Problem("amount_above", "lots", "review"));
    }

    [Fact]
    public void Webhook_signatures_match_the_documented_recipe()
    {
        // HMAC-SHA256("whsec_test", "1760900120.{}") in lowercase hex.
        var sig = WebhookService.Sign("whsec_test", 1760900120, "{}");
        Assert.StartsWith("t=1760900120,v1=", sig);
        Assert.Equal(64, sig.Split("v1=")[1].Length);
        Assert.NotEqual(sig, WebhookService.Sign("whsec_other", 1760900120, "{}"));
    }

    [Theory]
    [InlineData("127.0.0.1", true)]
    [InlineData("10.1.2.3", true)]
    [InlineData("172.17.0.1", true)]
    [InlineData("192.168.1.10", true)]
    [InlineData("169.254.169.254", true)]
    [InlineData("::1", true)]
    [InlineData("fd00::1", true)]
    [InlineData("8.8.8.8", false)]
    [InlineData("172.32.0.1", false)]
    public void Webhooks_never_reach_private_networks_by_default(string ip, bool isPrivate) => Assert.Equal(isPrivate, WebhookService.IsPrivate(IPAddress.Parse(ip)));

    [Fact]
    public void Webhook_urls_must_be_https_in_live() =>
        Assert.NotNull(WebhookService.UrlProblem("http://acme.in/hook", livemode: true));

    [Theory]
    [InlineData("sk_test_acme_AbC", "secret", false, "acme")]
    [InlineData("sk_live_acme-learning_AbC", "secret", true, "acme-learning")]
    [InlineData("tdk_acme_AbC", "developer", false, "acme")]
    public void Keys_name_their_tenant(string key, string kind, bool live, string slug) =>
        Assert.Equal((kind, live, slug), TenantKeyService.Parse(key));

    [Fact]
    public void Other_tokens_are_not_tenant_keys()
    {
        Assert.Null(TenantKeyService.Parse("pdk_abc"));
        Assert.Null(TenantKeyService.Parse("sk_test_nokey"));
    }

    [Fact]
    public void Next_actions_survive_a_round_trip()
    {
        NextAction[] actions =
        [
            new NextAction.Redirect("r1", "https://gw.example/pay"),
            new NextAction.PostForm("r2", "https://gw.example/form", new Dictionary<string, string> { ["hash"] = "abc" }),
            new NextAction.ShowQr("r3", "upi://pay?pa=a@b"),
            new NextAction.Sdk("r4", "payload"),
        ];
        foreach (var a in actions)
        {
            var back = NextActionJson.FromJson(NextActionJson.ToJson(a))!;
            Assert.Equal(a.GetType(), back.GetType());
            Assert.Equal(a.GatewayReference, back.GatewayReference);
        }
        Assert.Equal("abc", ((NextAction.PostForm)NextActionJson.FromJson(NextActionJson.ToJson(actions[1]))!).Fields["hash"]);
    }
}
