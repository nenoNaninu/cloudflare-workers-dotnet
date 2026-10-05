using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Cloudflare.Workers.Hosting.IntegrationTests;

/// <summary>
/// A local HTTP server the worker calls with <c>fetch</c>, so the tests never depend on the internet.
/// </summary>
public sealed class UpstreamServer : IAsyncDisposable
{
    private readonly WebApplication _app;

    private UpstreamServer(WebApplication app, string baseUrl)
    {
        _app = app;
        BaseUrl = baseUrl;
    }

    public string BaseUrl { get; }

    public static async Task<UpstreamServer> StartAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        var app = builder.Build();

        app.Map("/echo", async (HttpContext context) =>
        {
            using var buffer = new MemoryStream();
            await context.Request.Body.CopyToAsync(buffer);
            var body = buffer.ToArray();

            var headers = context.Request.Headers.ToDictionary(h => h.Key.ToLowerInvariant(), h => h.Value.ToString());
            context.Response.Headers["x-upstream"] = "yes";
            return Results.Json(new
            {
                method = context.Request.Method,
                path = context.Request.Path.Value,
                query = context.Request.QueryString.Value,
                headers,
                body = Encoding.UTF8.GetString(body),
                bodyBase64 = Convert.ToBase64String(body),
            });
        });

        app.MapGet("/headers", (HttpContext context) =>
        {
            context.Response.Headers["x-upstream"] = "yes";
            context.Response.Headers.Append("x-multi", "a");
            context.Response.Headers.Append("x-multi", "b");
            return Results.Text("headers", "text/plain");
        });

        app.MapGet("/redirect", () => Results.Redirect("/echo?redirected=1"));

        app.MapGet("/bytes", () => Results.Bytes(Enumerable.Range(0, 256).Select(i => (byte)i).ToArray(), "application/octet-stream"));

        app.MapGet("/json", () => Results.Text("""{"name":"Ada","age":36}""", "application/json"));

        await app.StartAsync();
        string address = app.Urls.First();
        return new UpstreamServer(app, address.TrimEnd('/'));
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}
