namespace Cloudflare.Workers.Hosting;

public interface IWorkerScheduledHandler
{
    Task ScheduledAsync(ScheduledEvent scheduledEvent, Env env, WorkerContext context);
}
