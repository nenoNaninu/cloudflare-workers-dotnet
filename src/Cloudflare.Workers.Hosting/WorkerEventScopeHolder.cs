namespace Cloudflare.Workers.Hosting;

internal sealed class WorkerEventScopeHolder
{
    private Env? _env;
    private WorkerContext? _context;

    public Env Env => _env ?? throw NotInEventScope(nameof(Hosting.Env));

    public WorkerContext WorkerContext => _context ?? throw NotInEventScope(nameof(Hosting.WorkerContext));

    public void Set(Env env, WorkerContext context)
    {
        _env = env;
        _context = context;
    }

    private static InvalidOperationException NotInEventScope(string name)
        => new($"{name} can only be resolved from the service scope of a worker event.");
}
