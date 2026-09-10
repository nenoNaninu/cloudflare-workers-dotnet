using Cloudflare.Workers.Hosting.Interop;
using Cloudflare.Workers.Hosting.Tests.Fakes;
using Xunit;

namespace Cloudflare.Workers.Hosting.Tests;

/// <summary>
/// End-to-end dispatch through the handler hosts: fake shim -> export entry ->
/// user handler -> promise resolution. Uses <see cref="JsRuntime.SetCurrent"/> and
/// shares the "HandlerHostState" collection with <see cref="WorkerApplicationTests"/>
/// so the process-wide state is never touched concurrently.
/// </summary>
[Collection("HandlerHostState")]
public class HandlerHostTests : IDisposable
{
    private sealed class EchoWorker : IWorkerFetchHandler
    {
        public async Task<HttpResponse> FetchAsync(HttpRequest request, Env env, WorkerContext context)
        {
            string body = await request.ReadAsStringAsync();
            return HttpResponse.Text($"echo:{body}");
        }
    }

    private sealed class KvWorker : IWorkerFetchHandler
    {
        public async Task<HttpResponse> FetchAsync(HttpRequest request, Env env, WorkerContext context)
        {
            using var kv = env.Kv("STORE");
            string? value = await kv.GetTextAsync("answer");
            return HttpResponse.Text(value ?? "missing");
        }
    }

    private sealed class FailingWorker : IWorkerFetchHandler
    {
        public Task<HttpResponse> FetchAsync(HttpRequest request, Env env, WorkerContext context)
            => throw new InvalidOperationException("handler exploded");
    }

    private sealed class CronWorker : IWorkerScheduledHandler
    {
        public string? SeenCron { get; private set; }

        public Task ScheduledAsync(ScheduledEvent scheduledEvent, Env env, WorkerContext context)
        {
            SeenCron = scheduledEvent.Cron;
            return Task.CompletedTask;
        }
    }

    public HandlerHostTests()
    {
        WorkerFetchHandlerHost.Reset();
        WorkerScheduledHandlerHost.Reset();
    }

    public void Dispose()
    {
        WorkerFetchHandlerHost.Reset();
        WorkerScheduledHandlerHost.Reset();
    }

    private static (FakeJsInterop Interop, JsRuntime Runtime) CreateWorld()
    {
        var world = FakeWorld.Create();
        JsRuntime.SetCurrent(world.Runtime);
        return world;
    }

    private static PromiseId DispatchFetch(FakeJsInterop interop, IWorkerFetchHandler handler, FakeObject request, FakeObject? envBindings = null)
    {
        WorkerFetchHandlerHost.Register(handler);
        PromiseId promiseId = interop.PromiseNew();
        WorkerFetchHandlerHost.Dispatch(
            interop.Retain(request),
            interop.Retain(envBindings ?? []),
            interop.Retain(new FakeObject()),
            promiseId);
        return promiseId;
    }

    private static FakePromise WaitForSettlement(FakeJsInterop interop, PromiseId promiseId)
    {
        var promise = interop.Promises[promiseId];
        // Continuations normally run inline; spin briefly in case one hopped threads.
        SpinWait.SpinUntil(() => promise.State != FakePromise.PromiseState.Pending, TimeSpan.FromSeconds(5));
        Assert.NotEqual(FakePromise.PromiseState.Pending, promise.State);
        return promise;
    }

    [Fact]
    public void DispatchFetch_ResolvesPromiseWithJsResponse()
    {
        var (interop, _) = CreateWorld();
        PromiseId promiseId = DispatchFetch(interop, new EchoWorker(), FakeWorld.CreateRequest("POST", "https://x.dev/", body: "ping"));

        var promise = WaitForSettlement(interop, promiseId);

        Assert.Equal(FakePromise.PromiseState.Fulfilled, promise.State);
        var response = Assert.IsType<FakeObject>(promise.Value);
        Assert.Equal("Response", response["__type"]);
        Assert.Equal("echo:ping", response["body"]);
        Assert.Equal(200d, response["status"]);
        Assert.Equal(0, interop.LiveHandleCount);
    }

    [Fact]
    public void DispatchFetch_HandlerCanUseEnvBindings()
    {
        var (interop, _) = CreateWorld();
        var bindings = new FakeObject
        {
            ["STORE"] = FakeWorld.CreateKv(new Dictionary<string, object?> { ["answer"] = "42" }),
        };
        PromiseId promiseId = DispatchFetch(interop, new KvWorker(), FakeWorld.CreateRequest("GET", "https://x.dev/"), bindings);

        var promise = WaitForSettlement(interop, promiseId);

        Assert.Equal(FakePromise.PromiseState.Fulfilled, promise.State);
        Assert.Equal("42", Assert.IsType<FakeObject>(promise.Value)["body"]);
    }

    [Fact]
    public void DispatchFetch_HandlerExceptionRejectsPromiseAndLogs()
    {
        var (interop, _) = CreateWorld();
        PromiseId promiseId = DispatchFetch(interop, new FailingWorker(), FakeWorld.CreateRequest("GET", "https://x.dev/"));

        var promise = WaitForSettlement(interop, promiseId);

        Assert.Equal(FakePromise.PromiseState.Rejected, promise.State);
        var error = Assert.IsType<FakeObject>(promise.Value);
        Assert.Equal("handler exploded", error["message"]);
        Assert.Contains(interop.Logs, log => log.Level == ConsoleLevel.Error && log.Message.Contains("handler exploded"));
        Assert.Equal(0, interop.LiveHandleCount);
    }

    [Fact]
    public void DispatchScheduled_ResolvesPromiseAfterHandlerRuns()
    {
        var (interop, _) = CreateWorld();
        var worker = new CronWorker();
        var controller = new FakeObject
        {
            ["cron"] = "*/5 * * * *",
            ["scheduledTime"] = 1_700_000_000_000d,
        };

        WorkerScheduledHandlerHost.Register(worker);
        PromiseId promiseId = interop.PromiseNew();
        WorkerScheduledHandlerHost.Dispatch(
            interop.Retain(controller),
            interop.Retain(new FakeObject()),
            interop.Retain(new FakeObject()),
            promiseId);

        var promise = WaitForSettlement(interop, promiseId);

        Assert.Equal(FakePromise.PromiseState.Fulfilled, promise.State);
        Assert.Equal("*/5 * * * *", worker.SeenCron);
        Assert.Equal(0, interop.LiveHandleCount);
    }

    [Fact]
    public void Register_AllowsIndependentFetchAndScheduledHandlers()
    {
        var (interop, _) = CreateWorld();
        var scheduledHandler = new CronWorker();
        WorkerFetchHandlerHost.Register(new EchoWorker());
        WorkerScheduledHandlerHost.Register(scheduledHandler);

        PromiseId fetchPromiseId = interop.PromiseNew();
        WorkerFetchHandlerHost.Dispatch(
            interop.Retain(FakeWorld.CreateRequest("POST", "https://x.dev/", body: "ping")),
            interop.Retain(new FakeObject()),
            interop.Retain(new FakeObject()),
            fetchPromiseId);

        PromiseId scheduledPromiseId = interop.PromiseNew();
        WorkerScheduledHandlerHost.Dispatch(
            interop.Retain(new FakeObject { ["cron"] = "*/10 * * * *" }),
            interop.Retain(new FakeObject()),
            interop.Retain(new FakeObject()),
            scheduledPromiseId);

        var fetchPromise = WaitForSettlement(interop, fetchPromiseId);
        var scheduledPromise = WaitForSettlement(interop, scheduledPromiseId);
        Assert.Equal("echo:ping", Assert.IsType<FakeObject>(fetchPromise.Value)["body"]);
        Assert.Equal(FakePromise.PromiseState.Fulfilled, scheduledPromise.State);
        Assert.Equal("*/10 * * * *", scheduledHandler.SeenCron);
        Assert.Equal(0, interop.LiveHandleCount);
    }
}
