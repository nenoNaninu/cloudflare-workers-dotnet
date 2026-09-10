using Cloudflare.Workers.Hosting.Interop;
using Cloudflare.Workers.Hosting.Tests.Fakes;
using Xunit;

namespace Cloudflare.Workers.Hosting.Tests;

/// <summary>
/// Uses the process-wide handler host registrations and JsRuntime.Current, so it shares
/// a collection with <see cref="HandlerHostTests"/> to avoid parallel interference.
/// </summary>
[Collection("HandlerHostState")]
public class WorkerApplicationTests : IDisposable
{
    public WorkerApplicationTests()
    {
        WorkerFetchHandlerHost.Reset();
        WorkerScheduledHandlerHost.Reset();
    }

    public void Dispose()
    {
        WorkerFetchHandlerHost.Reset();
        WorkerScheduledHandlerHost.Reset();
    }

    private static async Task<string> BodyOf(WorkerApplication app, string method, string url, string? body = null)
    {
        var (interop, runtime) = FakeWorld.Create();
        using var request = HttpRequest.FromJsObject(runtime.WrapToJsObject(interop.Retain(FakeWorld.CreateRequest(method, url, body: body))));
        using var env = FakeWorld.CreateEnv(runtime, interop);
        using var context = FakeWorld.CreateContext(runtime, interop);
        var response = await app.FetchAsync(request, env, context);
        using var js = response.ToJsObject(runtime);
        return Assert.IsType<string>(Assert.IsType<FakeObject>(interop.Get(js.Handle))["body"]!);
    }

    [Fact]
    public async Task Builder_MapsRoutesByMethodAndPattern()
    {
        var builder = WorkerApplication.CreateBuilder();
        builder.MapGet("/", static _ => Task.FromResult(HttpResponse.Text("root")));
        builder.MapGet("/users/:id", static context => Task.FromResult(HttpResponse.Text($"user {context.Parameters["id"]}")));
        builder.MapPost("/echo", static async context => HttpResponse.Text(await context.Request.ReadAsStringAsync()));
        var app = builder.Build();

        Assert.Equal("root", await BodyOf(app, "GET", "https://x.dev/"));
        Assert.Equal("user 7", await BodyOf(app, "GET", "https://x.dev/users/7"));
        Assert.Equal("ping", await BodyOf(app, "POST", "https://x.dev/echo", body: "ping"));
    }

    [Fact]
    public async Task Builder_FallbackAndNotFound()
    {
        var app = WorkerApplication.CreateBuilder().Build();
        var (interop, runtime) = FakeWorld.Create();
        using var request = HttpRequest.FromJsObject(runtime.WrapToJsObject(interop.Retain(FakeWorld.CreateRequest("GET", "https://x.dev/none"))));
        using var env = FakeWorld.CreateEnv(runtime, interop);
        using var context = FakeWorld.CreateContext(runtime, interop);

        var response = await app.FetchAsync(request, env, context);

        Assert.Equal(404, response.StatusCode);
    }

    [Fact]
    public async Task Builder_OnScheduledRunsHandler()
    {
        string? seenCron = null;
        var app = WorkerApplication.CreateBuilder()
            .OnScheduled((evt, _, _) =>
            {
                seenCron = evt.Cron;
                return Task.CompletedTask;
            })
            .Build();

        var (interop, runtime) = FakeWorld.Create();
        using var evt = ScheduledEvent.FromJsObject(runtime.WrapToJsObject(interop.Retain(new FakeObject { ["cron"] = "0 * * * *" })));
        using var env = FakeWorld.CreateEnv(runtime, interop);
        using var context = FakeWorld.CreateContext(runtime, interop);

        await app.ScheduledAsync(evt, env, context);

        Assert.Equal("0 * * * *", seenCron);
    }

    [Fact]
    public void Run_RegistersForDispatch()
    {
        var world = FakeWorld.Create();
        JsRuntime.SetCurrent(world.Runtime);
        var interop = world.Interop;

        var builder = WorkerApplication.CreateBuilder();
        builder.MapGet("/", static _ => Task.FromResult(HttpResponse.Text("registered!")));
        builder.Build().Run();

        PromiseId promiseId = interop.PromiseNew();
        WorkerFetchHandlerHost.Dispatch(
            interop.Retain(FakeWorld.CreateRequest("GET", "https://x.dev/")),
            interop.Retain(new FakeObject()),
            interop.Retain(new FakeObject()),
            promiseId);

        var promise = interop.Promises[promiseId];
        SpinWait.SpinUntil(() => promise.State != FakePromise.PromiseState.Pending, TimeSpan.FromSeconds(5));
        Assert.Equal(FakePromise.PromiseState.Fulfilled, promise.State);
        Assert.Equal("registered!", Assert.IsType<FakeObject>(promise.Value)["body"]);
    }

    [Fact]
    public void Run_RegistersScheduledHandlerForDispatch()
    {
        var world = FakeWorld.Create();
        JsRuntime.SetCurrent(world.Runtime);
        var interop = world.Interop;
        string? seenCron = null;

        WorkerApplication.CreateBuilder()
            .OnScheduled((scheduledEvent, _, _) =>
            {
                seenCron = scheduledEvent.Cron;
                return Task.CompletedTask;
            })
            .Build()
            .Run();

        PromiseId promiseId = interop.PromiseNew();
        WorkerScheduledHandlerHost.Dispatch(
            interop.Retain(new FakeObject { ["cron"] = "0 0 * * *" }),
            interop.Retain(new FakeObject()),
            interop.Retain(new FakeObject()),
            promiseId);

        var promise = interop.Promises[promiseId];
        SpinWait.SpinUntil(() => promise.State != FakePromise.PromiseState.Pending, TimeSpan.FromSeconds(5));
        Assert.Equal(FakePromise.PromiseState.Fulfilled, promise.State);
        Assert.Equal("0 0 * * *", seenCron);
        Assert.Equal(0, interop.LiveHandleCount);
    }

    [Fact]
    public void Register_TwiceThrows()
    {
        WorkerApplication.CreateBuilder().Build().Run();

        var exception = Assert.Throws<InvalidOperationException>(
            () => WorkerApplication.CreateBuilder().Build().Run());
        Assert.Contains("already registered", exception.Message);
    }

    [Fact]
    public void Dispatch_WithoutRegistrationRejectsWithGuidance()
    {
        var world = FakeWorld.Create();
        JsRuntime.SetCurrent(world.Runtime);
        var interop = world.Interop;

        PromiseId promiseId = interop.PromiseNew();
        WorkerFetchHandlerHost.Dispatch(
            interop.Retain(FakeWorld.CreateRequest("GET", "https://x.dev/")),
            interop.Retain(new FakeObject()),
            interop.Retain(new FakeObject()),
            promiseId);

        var promise = interop.Promises[promiseId];
        Assert.Equal(FakePromise.PromiseState.Rejected, promise.State);
        Assert.Contains("WorkerApplication.Run()", Assert.IsType<FakeObject>(promise.Value)["message"]?.ToString());
        Assert.Equal(0, interop.LiveHandleCount);
    }
}
