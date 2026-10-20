using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace PayLink.Tests.Fakes;

/// <summary>A merchant's webhook endpoint: records every delivery and answers with <see cref="Status"/>.</summary>
public sealed class WebhookReceiver : IAsyncDisposable
{
    public sealed record Delivery(string Signature, string EventType, string Body)
    {
        public JsonObject Json => JsonNode.Parse(Body)!.AsObject();
    }

    private readonly WebApplication _app;
    public ConcurrentQueue<Delivery> Received { get; } = new();
    public int Status { get; set; } = 200;
    public string Url { get; }

    private WebhookReceiver(WebApplication app, string url) { _app = app; Url = url; }

    public static async Task<WebhookReceiver> StartAsync()
    {
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        var url = $"http://127.0.0.1:{port}";
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls(url);
        builder.Logging.ClearProviders();
        var app = builder.Build();
        var receiver = new WebhookReceiver(app, url + "/hooks/paylink");
        app.MapPost("/hooks/paylink", async (HttpContext http) =>
        {
            var body = await new StreamReader(http.Request.Body).ReadToEndAsync();
            receiver.Received.Enqueue(new Delivery(http.Request.Headers["PayLink-Signature"].ToString(), http.Request.Headers["PayLink-Event"].ToString(), body));
            return Results.Text(receiver.Status == 200 ? "ok" : "nope", statusCode: receiver.Status);
        });
        await app.StartAsync();
        return receiver;
    }

    public async ValueTask DisposeAsync() => await _app.DisposeAsync();
}
