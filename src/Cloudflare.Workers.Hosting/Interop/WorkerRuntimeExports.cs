using System.Runtime.InteropServices;

namespace Cloudflare.Workers.Hosting.Interop;

internal static class WorkerRuntimeExports
{
    [UnmanagedCallersOnly(EntryPoint = "cf_callback_invoke")]
    internal static void CallbackInvoke(int callbackId, int argumentsHandle)
    {
        try
        {
            CallbackRegistry.Invoke(callbackId, new JsHandle(argumentsHandle));
        }
        catch (Exception ex)
        {
            TryLogFatal(ex);
        }
    }

    [UnmanagedCallersOnly(EntryPoint = "cf_promise_complete")]
    internal static void PromiseComplete(int continuationId, int isSuccess, int valueHandle)
    {
        try
        {
            ContinuationRegistry.Complete(continuationId, isSuccess != 0, new JsHandle(valueHandle));
        }
        catch (Exception ex)
        {
            TryLogFatal(ex);
        }
    }

    [UnmanagedCallersOnly(EntryPoint = "cf_timeout_fired")]
    internal static void TimeoutFired(int continuationId)
    {
        try
        {
            ContinuationRegistry.Complete(continuationId, true, JsHandle.Undefined);
        }
        catch (Exception ex)
        {
            TryLogFatal(ex);
        }
    }

    private static void TryLogFatal(Exception ex)
    {
        try
        {
            JsRuntime.Current.Interop.Log(ConsoleLevel.Error, $"[Cloudflare.Workers.Hosting] unhandled callback or continuation exception: {ex}");
        }
        catch
        {
            // Nothing left to report to.
        }
    }
}
