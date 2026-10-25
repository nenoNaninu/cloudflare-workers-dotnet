using Cloudflare.Workers.Hosting.Tests.Fakes;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Cloudflare.Workers.Hosting.Tests;

public class DependencyInjectionTests
{
    private sealed class WorkerCache
    {
        private readonly Dictionary<string, string> _values = new();

        public string GetOrAdd(string key, Func<string> factory)
        {
            if (!_values.TryGetValue(key, out var value))
            {
                value = factory();
                _values[key] = value;
            }

            return value;
        }
    }

    private sealed class ScopedTracker : IDisposable
    {
        public static int DisposedCount;

        public Guid Id { get; } = Guid.NewGuid();

        public void Dispose() => Interlocked.Increment(ref DisposedCount);
    }

    private sealed class Greeter(Env env)
    {
        public string Greet() => $"hello {env.Var("NAME")}";
    }

    private sealed class SingletonNeedsEnv(Env env)
    {
        public Env Env { get; } = env;
    }

    private static async Task<string> FetchBody(WorkerApplication app, string url, FakeObject? bindings = null)
    {
        var (interop, runtime) = FakeWorld.Create();
        using var request = HttpRequest.FromJsObject(runtime.WrapToJsObject(interop.Retain(FakeWorld.CreateRequest("GET", url))));
        using var env = FakeWorld.CreateEnv(runtime, interop, bindings);
        using var context = FakeWorld.CreateContext(runtime, interop);
        var response = await app.FetchAsync(request, env, context);
        using var js = response.ToJsObject(runtime);
        return Assert.IsType<string>(Assert.IsType<FakeObject>(interop.Get(js.Handle))["body"]!);
    }

    [Fact]
    public async Task SingletonCache_IsReusedWithinSameApplication()
    {
        var builder = WorkerApplication.CreateBuilder();
        builder.Services.AddSingleton<WorkerCache>();
        builder.MapGet("/", static context =>
        {
            var cache = context.RequestServices.GetRequiredService<WorkerCache>();
            return Task.FromResult(HttpResponse.Text(cache.GetOrAdd("greeting", static () => Guid.NewGuid().ToString())));
        });
        var app = builder.Build();

        string cachedValue = await FetchBody(app, "https://x.dev/");
        Assert.True(Guid.TryParse(cachedValue, out _));
        Assert.Equal(cachedValue, await FetchBody(app, "https://x.dev/"));
        Assert.Equal(cachedValue, app.Services.GetRequiredService<WorkerCache>().GetOrAdd("greeting", static () => "cache miss"));
    }

    [Fact]
    public void SingletonCache_IsNotSharedAcrossApplications()
    {
        static WorkerApplication CreateApplication()
        {
            var builder = WorkerApplication.CreateBuilder();
            builder.Services.AddSingleton<WorkerCache>();
            return builder.Build();
        }

        var first = CreateApplication().Services.GetRequiredService<WorkerCache>();
        var second = CreateApplication().Services.GetRequiredService<WorkerCache>();

        Assert.Equal("first", first.GetOrAdd("greeting", static () => "first"));
        Assert.Equal("second", second.GetOrAdd("greeting", static () => "second"));
    }

    [Fact]
    public async Task Scoped_IsCreatedPerRequestAndDisposedAfterIt()
    {
        var builder = WorkerApplication.CreateBuilder();
        builder.Services.AddScoped<ScopedTracker>();
        builder.MapGet("/", static context =>
        {
            var first = context.RequestServices.GetRequiredService<ScopedTracker>();
            var second = context.RequestServices.GetRequiredService<ScopedTracker>();
            Assert.Same(first, second);
            return Task.FromResult(HttpResponse.Text(first.Id.ToString()));
        });
        var app = builder.Build();

        int disposedBefore = Volatile.Read(ref ScopedTracker.DisposedCount);
        string id1 = await FetchBody(app, "https://x.dev/");
        string id2 = await FetchBody(app, "https://x.dev/");

        Assert.NotEqual(id1, id2);
        Assert.Equal(disposedBefore + 2, Volatile.Read(ref ScopedTracker.DisposedCount));
    }

    [Fact]
    public async Task Scoped_CanInjectCurrentEnv()
    {
        var builder = WorkerApplication.CreateBuilder();
        builder.Services.AddScoped<Greeter>();
        builder.MapGet("/", static context =>
            Task.FromResult(HttpResponse.Text(context.RequestServices.GetRequiredService<Greeter>().Greet())));
        var app = builder.Build();

        Assert.Equal("hello alice", await FetchBody(app, "https://x.dev/", new FakeObject { ["NAME"] = "alice" }));
        Assert.Equal("hello bob", await FetchBody(app, "https://x.dev/", new FakeObject { ["NAME"] = "bob" }));
    }

    [Fact]
    public async Task RequestServices_ResolvesSameEnvAndContextAsHttpContext()
    {
        var builder = WorkerApplication.CreateBuilder();
        builder.MapGet("/", static context =>
        {
            Assert.Same(context.Env, context.RequestServices.GetRequiredService<Env>());
            Assert.Same(context.WorkerContext, context.RequestServices.GetRequiredService<WorkerContext>());
            return Task.FromResult(HttpResponse.Text("ok"));
        });
        var app = builder.Build();

        Assert.Equal("ok", await FetchBody(app, "https://x.dev/"));
    }

    [Fact]
    public async Task Fallback_ReceivesRequestServices()
    {
        var builder = WorkerApplication.CreateBuilder();
        var cache = new WorkerCache();
        cache.GetOrAdd("fallback", static () => "cached fallback");
        builder.Services.AddSingleton(cache);
        builder.MapFallback(static context =>
            Task.FromResult(HttpResponse.Text(context.RequestServices.GetRequiredService<WorkerCache>().GetOrAdd("fallback", static () => "cache miss"), 404)));
        var app = builder.Build();

        Assert.Equal("cached fallback", await FetchBody(app, "https://x.dev/missing"));
    }

    [Fact]
    public void Env_CannotBeResolvedFromRootProvider()
    {
        var app = WorkerApplication.CreateBuilder().Build();

        Assert.Throws<InvalidOperationException>(() => app.Services.GetRequiredService<Env>());
    }

    [Fact]
    public void Env_CannotBeCapturedBySingleton()
    {
        var builder = WorkerApplication.CreateBuilder();
        builder.Services.AddSingleton<SingletonNeedsEnv>();
        var app = builder.Build();

        Assert.Throws<InvalidOperationException>(() => app.Services.GetRequiredService<SingletonNeedsEnv>());
    }

    [Fact]
    public async Task OnScheduled_ReceivesScopedServices()
    {
        string? greeting = null;
        var builder = WorkerApplication.CreateBuilder();
        builder.Services.AddScoped<Greeter>();
        builder.OnScheduled((_, _, _, services) =>
        {
            greeting = services.GetRequiredService<Greeter>().Greet();
            return Task.CompletedTask;
        });
        var app = builder.Build();

        var (interop, runtime) = FakeWorld.Create();
        using var evt = ScheduledEvent.FromJsObject(runtime.WrapToJsObject(interop.Retain(new FakeObject { ["cron"] = "0 * * * *" })));
        using var env = FakeWorld.CreateEnv(runtime, interop, new FakeObject { ["NAME"] = "cron" });
        using var context = FakeWorld.CreateContext(runtime, interop);

        await app.ScheduledAsync(evt, env, context);

        Assert.Equal("hello cron", greeting);
    }
}
