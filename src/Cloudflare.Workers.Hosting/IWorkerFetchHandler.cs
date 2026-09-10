namespace Cloudflare.Workers.Hosting;

public interface IWorkerFetchHandler
{
    Task<HttpResponse> FetchAsync(HttpRequest request, Env env, WorkerContext context);
}
