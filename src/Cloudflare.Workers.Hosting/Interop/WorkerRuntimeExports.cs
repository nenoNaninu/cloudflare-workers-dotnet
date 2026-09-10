using System.Runtime.InteropServices;

namespace Cloudflare.Workers.Hosting.Interop;

internal static class WorkerRuntimeExports
{
    [UnmanagedCallersOnly(EntryPoint = "cf_promise_complete")]
    internal static void PromiseComplete(int callbackId, int success, int valueHandle)
    {
        try
        {
            CallbackRegistry.Complete(callbackId, success != 0, new JsHandle(valueHandle));
        }
        catch (Exception ex)
        {
            TryLogFatal(ex);
        }
    }

    [UnmanagedCallersOnly(EntryPoint = "cf_timeout_fired")]
    internal static void TimeoutFired(int callbackId)
    {
        try
        {
            CallbackRegistry.Complete(callbackId, true, JsHandle.Undefined);
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
            JsRuntime.Current.Interop.Log(ConsoleLevel.Error, $"[Cloudflare.Workers.Hosting] unhandled callback exception: {ex}");
        }
        catch
        {
            // Nothing left to report to.
        }
    }
}
