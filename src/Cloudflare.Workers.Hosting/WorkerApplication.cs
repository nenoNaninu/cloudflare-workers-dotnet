namespace Cloudflare.Workers.Hosting;


public sealed class WorkerApplication : IWorkerFetchHandler, IWorkerScheduledHandler
{
    private readonly HttpRequestRouter _router;
    private readonly Func<ScheduledEvent, Env, WorkerContext, Task>? _scheduled;

    internal WorkerApplication(HttpRequestRouter router, Func<ScheduledEvent, Env, WorkerContext, Task>? scheduled)
    {
        _router = router;
        _scheduled = scheduled;
    }

    public static WorkerApplicationBuilder CreateBuilder() => new();

    /// <summary>
    /// Registers this application as the worker's event handler and returns.
    /// Unlike ASP.NET Core's <c>Run</c>, this does not block:
    /// the wasm module stays alive after Main returns and the runtime dispatches
    /// into the registered application per event.
    /// </summary>
    public void Run()
    {
        if (_router is not null)
        {
            WorkerFetchHandlerHost.Register(this);
        }

        if (_scheduled is not null)
        {
            WorkerScheduledHandlerHost.Register(this);
        }
    }

    public Task<HttpResponse> FetchAsync(HttpRequest request, Env env, WorkerContext context)
        => _router.HandleAsync(request, env, context);

    public Task ScheduledAsync(ScheduledEvent scheduledEvent, Env env, WorkerContext context)
        => _scheduled is null ? Task.CompletedTask : _scheduled(scheduledEvent, env, context);
}

public sealed class WorkerApplicationBuilder
{
    private readonly HttpRequestRouter _router = new();
    private Func<ScheduledEvent, Env, WorkerContext, Task>? _scheduled;

    internal WorkerApplicationBuilder()
    {
    }

    public WorkerApplicationBuilder MapGet(string pattern, HttpRequestHandler handler)
    {
        _router.Get(pattern, handler);
        return this;
    }

    public WorkerApplicationBuilder MapPost(string pattern, HttpRequestHandler handler)
    {
        _router.Post(pattern, handler);
        return this;
    }

    public WorkerApplicationBuilder MapPut(string pattern, HttpRequestHandler handler)
    {
        _router.Put(pattern, handler);
        return this;
    }

    public WorkerApplicationBuilder MapPatch(string pattern, HttpRequestHandler handler)
    {
        _router.Patch(pattern, handler);
        return this;
    }

    public WorkerApplicationBuilder MapDelete(string pattern, HttpRequestHandler handler)
    {
        _router.Delete(pattern, handler);
        return this;
    }

    public WorkerApplicationBuilder MapHead(string pattern, HttpRequestHandler handler)
    {
        _router.Head(pattern, handler);
        return this;
    }

    public WorkerApplicationBuilder MapOptions(string pattern, HttpRequestHandler handler)
    {
        _router.Options(pattern, handler);
        return this;
    }

    public WorkerApplicationBuilder Map(string pattern, HttpRequestHandler handler)
    {
        _router.All(pattern, handler);
        return this;
    }

    public WorkerApplicationBuilder MapFallback(HttpRequestHandler handler)
    {
        _router.Fallback(handler);
        return this;
    }

    public WorkerApplicationBuilder OnScheduled(Func<ScheduledEvent, Env, WorkerContext, Task> handler)
    {
        _scheduled = handler;
        return this;
    }

    public WorkerApplication Build() => new(_router, _scheduled);
}
