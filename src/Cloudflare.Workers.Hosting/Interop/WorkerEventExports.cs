using System.Runtime.InteropServices;

namespace Cloudflare.Workers.Hosting.Interop;

internal static class WorkerEventExports
{
    [UnmanagedCallersOnly(EntryPoint = "cf_fetch")]
    internal static void Fetch(int request, int env, int context, int promiseId)
    {
        try
        {
            WorkerFetchHandlerHost.Dispatch(
                new JsHandle(request),
                new JsHandle(env),
                new JsHandle(context),
                new PromiseId(promiseId)
            );
        }
        catch (Exception ex)
        {
            TryReject(promiseId, ex);
        }
    }

    [UnmanagedCallersOnly(EntryPoint = "cf_scheduled")]
    internal static void Scheduled(int scheduledEvent, int env, int context, int promiseId)
    {
        try
        {
            WorkerScheduledHandlerHost.Dispatch(
                new JsHandle(scheduledEvent),
                new JsHandle(env),
                new JsHandle(context),
                new PromiseId(promiseId)
            );
        }
        catch (Exception ex)
        {
            TryReject(promiseId, ex);
        }
    }

    private static void TryReject(int promiseId, Exception ex)
    {
        try
        {
            JsRuntime.Current.Interop.PromiseReject(new PromiseId(promiseId), ex.ToString());
        }
        catch
        {
            // The shim is unreachable; nothing more we can do.
        }
    }
}
