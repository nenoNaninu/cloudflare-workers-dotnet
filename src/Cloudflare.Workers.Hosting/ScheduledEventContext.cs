namespace Cloudflare.Workers.Hosting;

public sealed class ScheduledEventContext
{
    internal ScheduledEventContext(
        ScheduledEvent scheduledEvent,
        Env env,
        WorkerContext workerContext,
        IServiceProvider eventServices)
    {
        ScheduledEvent = scheduledEvent;
        Env = env;
        WorkerContext = workerContext;
        EventServices = eventServices;
    }

    public ScheduledEvent ScheduledEvent { get; }

    public Env Env { get; }

    public WorkerContext WorkerContext { get; }

    public IServiceProvider EventServices { get; }
}
