using Cloudflare.Workers.Hosting.Interop;

namespace Cloudflare.Workers.Hosting;

public static class WorkerTimer
{
    public static Task Delay(TimeSpan delay) => Delay(delay.TotalMilliseconds);

    public static Task Delay(double milliseconds) => Delay(JsRuntime.Current, milliseconds);

    public static Task Delay(JsRuntime runtime, double milliseconds)
    {
        var completion = new TaskCompletionSource();
        int callbackId = CallbackRegistry.Register((_, _) => completion.SetResult());
        runtime.Interop.SetTimeout(callbackId, milliseconds);
        return completion.Task;
    }
}
