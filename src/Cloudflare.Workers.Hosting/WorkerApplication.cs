using Microsoft.Extensions.DependencyInjection;

namespace Cloudflare.Workers.Hosting;


public sealed class WorkerApplication : IWorkerFetchHandler, IWorkerScheduledHandler
{
    private readonly HttpRequestRouter _router;
    private readonly Func<ScheduledEvent, Env, WorkerContext, IServiceProvider, Task>? _scheduled;

    internal WorkerApplication(
        IServiceProvider services,
        HttpRequestRouter router,
        Func<ScheduledEvent, Env, WorkerContext, IServiceProvider, Task>? scheduled)
    {
        Services = services;
        _router = router;
        _scheduled = scheduled;
    }

    public IServiceProvider Services { get; }

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

    public async Task<HttpResponse> FetchAsync(HttpRequest request, Env env, WorkerContext context)
    {
        await using var scope = CreateEventScope(env, context);
        return await _router.HandleAsync(request, env, context, scope.ServiceProvider).ConfigureAwait(false);
    }

    public async Task ScheduledAsync(ScheduledEvent scheduledEvent, Env env, WorkerContext context)
    {
        if (_scheduled is null)
        {
            return;
        }

        await using var scope = CreateEventScope(env, context);
        await _scheduled(scheduledEvent, env, context, scope.ServiceProvider).ConfigureAwait(false);
    }

    private AsyncServiceScope CreateEventScope(Env env, WorkerContext context)
    {
        var scope = Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<WorkerEventScopeHolder>().Set(env, context);
        return scope;
    }
}

public sealed class WorkerApplicationBuilder
{
    private readonly HttpRequestRouter _router = new();
    private Func<ScheduledEvent, Env, WorkerContext, IServiceProvider, Task>? _scheduled;

    internal WorkerApplicationBuilder()
    {
        Services.AddScoped(static _ => new WorkerEventScopeHolder());
        Services.AddScoped(static sp => sp.GetRequiredService<WorkerEventScopeHolder>().Env);
        Services.AddScoped(static sp => sp.GetRequiredService<WorkerEventScopeHolder>().WorkerContext);
    }

    public IServiceCollection Services { get; } = new ServiceCollection();

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
        ArgumentNullException.ThrowIfNull(handler);
        _scheduled = (scheduledEvent, env, context, _) => handler(scheduledEvent, env, context);
        return this;
    }

    /// <summary>
    /// Registers the scheduled handler. The last argument is the service provider of the event's scope.
    /// </summary>
    public WorkerApplicationBuilder OnScheduled(Func<ScheduledEvent, Env, WorkerContext, IServiceProvider, Task> handler)
    {
        _scheduled = handler;
        return this;
    }

    public WorkerApplication Build()
    {
        var services = Services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        return new(services, _router, _scheduled);
    }
}
