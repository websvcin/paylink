using System.Text.Json;
using System.Text.Json.Nodes;
using WebSvc.PayLink.Abstractions;

namespace PayLink.Payments;

/// <summary>Stores a connector's <see cref="NextAction"/> on the attempt, so the checkout can show it again after a reload.</summary>
public static class NextActionJson
{
    public static string ToJson(NextAction action) => (action switch
    {
        NextAction.Redirect r => new JsonObject { ["type"] = "redirect", ["ref"] = r.GatewayReference, ["url"] = r.Url },
        NextAction.PostForm f => new JsonObject
        {
            ["type"] = "post_form", ["ref"] = f.GatewayReference, ["url"] = f.Url,
            ["fields"] = new JsonObject(f.Fields.Select(kv => KeyValuePair.Create(kv.Key, (JsonNode?)JsonValue.Create(kv.Value)))),
        },
        NextAction.ShowQr q => new JsonObject { ["type"] = "qr", ["ref"] = q.GatewayReference, ["uri"] = q.Uri },
        NextAction.Sdk s => new JsonObject { ["type"] = "sdk", ["ref"] = s.GatewayReference, ["payload"] = s.Payload },
        NextAction.Failed f => new JsonObject { ["type"] = "failed", ["ref"] = f.GatewayReference, ["code"] = f.GatewayCode, ["message"] = f.GatewayMessage },
        _ => throw new NotSupportedException(action.GetType().Name),
    }).ToJsonString();

    public static NextAction? FromJson(string? json)
    {
        if (string.IsNullOrEmpty(json)) return null;
        var o = JsonNode.Parse(json)!.AsObject();
        string S(string key) => o[key]?.GetValue<string>() ?? "";
        return S("type") switch
        {
            "redirect" => new NextAction.Redirect(S("ref"), S("url")),
            "post_form" => new NextAction.PostForm(S("ref"), S("url"),
                o["fields"]!.AsObject().ToDictionary(kv => kv.Key, kv => kv.Value?.GetValue<string>() ?? "")),
            "qr" => new NextAction.ShowQr(S("ref"), S("uri")),
            "sdk" => new NextAction.Sdk(S("ref"), S("payload")),
            "failed" => new NextAction.Failed(S("ref"), FailureReason.GatewayError, S("code"), S("message")),
            _ => null,
        };
    }
}
