using Cloudflare.Workers.Hosting.Interop;

namespace Cloudflare.Workers.Hosting;

/// <summary>
/// https://developers.cloudflare.com/workers/runtime-apis/context/
/// </summary>
public sealed class WorkerContext : IDisposable
{
    private readonly JsObject _js;

    internal WorkerContext(JsObject js)
    {
        _js = js;
    }

    public JsObject Js => _js;

    /// <summary>
    /// https://developers.cloudflare.com/workers/runtime-apis/context/#waituntil
    /// </summary>
    public void WaitUntil(Task task)
    {
        var runtime = _js.Runtime;
        var interop = runtime.Interop;
        PromiseId promiseId = interop.PromiseNew();

        using (var promise = runtime.WrapToJsObject(interop.PromiseGet(promiseId)))
        {
            using var result = _js.Call("waitUntil", JsArg.From(promise));
        }

        _ = CompleteAsync(task, interop, promiseId);
    }

    /// <summary>
    /// https://developers.cloudflare.com/workers/runtime-apis/context/#passthroughonexception
    /// </summary>
    public void PassThroughOnException()
    {
        using var result = _js.Call("passThroughOnException");
    }

    private static async Task CompleteAsync(Task task, IJsInterop interop, PromiseId promiseId)
    {
        try
        {
            await task.ConfigureAwait(false);
            interop.PromiseResolve(promiseId, JsHandle.Undefined);
        }
        catch (Exception ex)
        {
            interop.PromiseReject(promiseId, ex.ToString());
        }
    }

    public void Dispose() => _js.Dispose();
}
