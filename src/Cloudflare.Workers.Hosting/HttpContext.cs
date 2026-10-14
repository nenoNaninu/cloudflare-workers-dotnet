namespace Cloudflare.Workers.Hosting;

public sealed class HttpContext
{
    internal HttpContext(
        HttpRequest request,
        Env env,
        WorkerContext executionContext,
        IReadOnlyDictionary<string, string> parameters,
        IServiceProvider requestServices)
    {
        Request = request;
        Env = env;
        WorkerContext = executionContext;
        Parameters = parameters;
        RequestServices = requestServices;
    }

    public HttpRequest Request { get; }

    public Env Env { get; }

    public WorkerContext WorkerContext { get; }

    public IReadOnlyDictionary<string, string> Parameters { get; }

    /// <summary>
    /// The service provider of this request's scope. Scoped services are disposed after the response is produced.
    /// </summary>
    public IServiceProvider RequestServices { get; }
}
