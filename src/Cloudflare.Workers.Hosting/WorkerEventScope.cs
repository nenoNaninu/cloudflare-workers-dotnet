namespace Cloudflare.Workers.Hosting;

/// <summary>
/// Scoped holder that exposes the current event's <see cref="Env"/> and <see cref="WorkerContext"/>
/// to services resolved from the event's service scope.
/// </summary>
internal sealed class WorkerEventScope
{
    private Env? _env;
    private WorkerContext? _context;

    public Env Env => _env ?? throw NotInEventScope(nameof(Hosting.Env));

    public WorkerContext WorkerContext => _context ?? throw NotInEventScope(nameof(Hosting.WorkerContext));

    public void Initialize(Env env, WorkerContext context)
    {
        _env = env;
        _context = context;
    }

    private static InvalidOperationException NotInEventScope(string name)
        => new($"{name} can only be resolved from the service scope of a fetch or scheduled event (e.g. HttpContext.RequestServices).");
}
