using System.Collections.Concurrent;
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
    private readonly ConcurrentDictionary<string, WaitUntilGate> _waitUntilGates;

    private UpstreamServer(WebApplication app, string baseUrl, ConcurrentDictionary<string, WaitUntilGate> waitUntilGates)
    {
        _app = app;
        BaseUrl = baseUrl;
        _waitUntilGates = waitUntilGates;
    }

    public string BaseUrl { get; }

    public WaitUntilGate CreateWaitUntilGate(string key)
    {
        var gate = new WaitUntilGate(() => _waitUntilGates.TryRemove(key, out _));
        if (!_waitUntilGates.TryAdd(key, gate))
        {
            throw new InvalidOperationException($"A waitUntil gate already exists for '{key}'.");
        }

        return gate;
    }

    public static async Task<UpstreamServer> StartAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        var app = builder.Build();
        var waitUntilGates = new ConcurrentDictionary<string, WaitUntilGate>();

        app.MapGet("/wait-until/{key}", async (string key, HttpContext context) =>
        {
            if (!waitUntilGates.TryGetValue(key, out var gate))
            {
                return Results.NotFound();
            }

            gate.MarkStarted();
            await gate.WaitForReleaseAsync(context.RequestAborted);
            return Results.NoContent();
        });

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
        return new UpstreamServer(app, address.TrimEnd('/'), waitUntilGates);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var gate in _waitUntilGates.Values)
        {
            gate.Dispose();
        }
        await _app.StopAsync();
        await _app.DisposeAsync();
    }

    public sealed class WaitUntilGate(Action remove) : IDisposable
    {
        private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Started => _started.Task;

        internal void MarkStarted() => _started.TrySetResult();

        internal Task WaitForReleaseAsync(CancellationToken cancellationToken)
            => _released.Task.WaitAsync(cancellationToken);

        public void Release() => _released.TrySetResult();

        public void Dispose()
        {
            Release();
            remove();
        }
    }
}
