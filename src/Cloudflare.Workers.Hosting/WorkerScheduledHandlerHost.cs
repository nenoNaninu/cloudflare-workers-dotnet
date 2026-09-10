using Cloudflare.Workers.Hosting.Interop;

namespace Cloudflare.Workers.Hosting;

internal static class WorkerScheduledHandlerHost
{
    private static IWorkerScheduledHandler? Handler;

    public static void Register(IWorkerScheduledHandler handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        if (Handler is not null)
        {
            throw new InvalidOperationException(
                "A scheduled handler is already registered. Call WorkerApplication.Run() (or ScheduledHandlerHost.Register) exactly once."
            );
        }

        Handler = handler;
    }

    internal static void Reset() => Handler = null;

    internal static void Dispatch(
        JsHandle eventHandle,
        JsHandle envHandle,
        JsHandle contextHandle,
        PromiseId promiseId)
    {
        var runtime = JsRuntime.Current;
        if (Handler is null)
        {
            using var scheduledEvent = runtime.WrapToJsObject(eventHandle);
            using var env = runtime.WrapToJsObject(envHandle);
            using var context = runtime.WrapToJsObject(contextHandle);
            runtime.Interop.PromiseReject(
                promiseId,
                "No handler is registered for 'scheduled' events. Call WorkerApplication.Run() from Main (or register an IScheduledHandler via ScheduledHandlerHost.Register)."
            );
            return;
        }

        _ = DispatchCoreAsync(Handler, eventHandle, envHandle, contextHandle, runtime, promiseId);
    }

    private static async Task DispatchCoreAsync(
        IWorkerScheduledHandler handler,
        JsHandle eventHandle,
        JsHandle envHandle,
        JsHandle contextHandle,
        JsRuntime runtime,
        PromiseId promiseId)
    {
        try
        {
            using var scheduledEvent = ScheduledEvent.FromJsObject(runtime.WrapToJsObject(eventHandle));
            using var env = new Env(runtime.WrapToJsObject(envHandle));
            using var context = new WorkerContext(runtime.WrapToJsObject(contextHandle));
            await handler.ScheduledAsync(scheduledEvent, env, context).ConfigureAwait(false);
            runtime.Interop.PromiseResolve(promiseId, JsHandle.Undefined);
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
            runtime.Interop.Log(ConsoleLevel.Error, $"[Cloudflare.Workers.Hosting] scheduled handler failed: {ex}");
            runtime.Interop.PromiseReject(promiseId, ex.Message);
        }
        catch
        {
            // The shim is unreachable; nothing more we can do.
        }
    }
}
