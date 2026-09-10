using Cloudflare.Workers.Hosting.Interop;

namespace Cloudflare.Workers.Hosting;

internal static class WorkerFetchHandlerHost
{
    private static IWorkerFetchHandler? Handler;

    public static void Register(IWorkerFetchHandler handler)
    {
        ArgumentNullException.ThrowIfNull(handler);

        if (Handler is not null)
        {
            throw new InvalidOperationException(
                "A fetch handler is already registered. Call WorkerApplication.Run() (or FetchHandlerHost.Register) exactly once."
            );
        }

        Handler = handler;
    }

    internal static void Reset() => Handler = null;

    internal static void Dispatch(
        JsHandle requestHandle,
        JsHandle envHandle,
        JsHandle contextHandle,
        PromiseId promiseId)
    {
        var runtime = JsRuntime.Current;
        if (Handler is null)
        {
            using var request = runtime.WrapToJsObject(requestHandle);
            using var env = runtime.WrapToJsObject(envHandle);
            using var context = runtime.WrapToJsObject(contextHandle);
            runtime.Interop.PromiseReject(
                promiseId,
                "No handler is registered for 'fetch' events. Call WorkerApplication.Run() from Main (or register an IFetchHandler via FetchHandlerHost.Register)."
            );
            return;
        }

        _ = DispatchCoreAsync(Handler, requestHandle, envHandle, contextHandle, runtime, promiseId);
    }

    private static async Task DispatchCoreAsync(
        IWorkerFetchHandler handler,
        JsHandle requestHandle,
        JsHandle envHandle,
        JsHandle contextHandle,
        JsRuntime runtime,
        PromiseId promiseId)
    {
        try
        {
            using var request = HttpRequest.FromJsObject(runtime.WrapToJsObject(requestHandle));
            using var env = new Env(runtime.WrapToJsObject(envHandle));
            using var context = new WorkerContext(runtime.WrapToJsObject(contextHandle));

            var response = await handler.FetchAsync(request, env, context).ConfigureAwait(false);

            using var jsResponse = response.ToJsObject(runtime);

            // Ownership of the response handle transfers to the shim.
            runtime.Interop.PromiseResolve(promiseId, jsResponse.MoveHandleOwnership());
        }
        catch (Exception ex)
        {
            RejectPromise(runtime, promiseId, ex);
        }
    }

    private static void RejectPromise(JsRuntime runtime, PromiseId promiseId, Exception ex)
    {
        try
        {
            runtime.Interop.Log(ConsoleLevel.Error, $"[Cloudflare.Workers.Hosting] fetch handler failed: {ex}");
            runtime.Interop.PromiseReject(promiseId, ex.Message);
        }
        catch
        {
            // The shim is unreachable; nothing more we can do.
        }
    }
}
