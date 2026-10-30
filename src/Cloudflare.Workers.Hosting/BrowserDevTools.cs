using System.Text.Json;
using Cloudflare.Workers.Hosting.Interop;

namespace Cloudflare.Workers.Hosting;

/// <summary>The nested Browser Run DevTools JSON binding.</summary>
public sealed class BrowserDevTools : IDisposable
{
    private readonly JsObject _js;

    internal BrowserDevTools(JsObject js) => _js = js;

    public JsObject Js => _js;

    public Task<BrowserVersion> GetVersionAsync(string sessionId)
        => BrowserInterop.CallAsync(_js, "getVersion", BrowserJsonContext.Default.BrowserVersion, JsArg.From(sessionId));

    /// <summary>Returns the extensible Chrome DevTools protocol definition as JSON.</summary>
    public Task<JsonElement> GetProtocolAsync(string sessionId)
        => BrowserInterop.CallAsync(_js, "getProtocol", BrowserJsonContext.Default.JsonElement, JsArg.From(sessionId));

    public async Task<BrowserTarget[]> ListTargetsAsync(string sessionId, BrowserTargetOptions? options = null)
    {
        using var init = BrowserInterop.Options(_js.Runtime, options, BrowserJsonContext.Default.BrowserTargetOptions);
        return await BrowserInterop.CallAsync(_js, "listTargets", BrowserJsonContext.Default.BrowserTargetArray,
            JsArg.From(sessionId), BrowserInterop.Optional(init)).ConfigureAwait(false);
    }

    public Task<BrowserTarget> GetTargetAsync(string sessionId, string targetId)
        => BrowserInterop.CallAsync(_js, "getTarget", BrowserJsonContext.Default.BrowserTarget,
            JsArg.From(sessionId), JsArg.From(targetId));

    public async Task<BrowserTarget> NewTargetAsync(string sessionId, string? url = null, BrowserTargetOptions? options = null)
    {
        using var init = BrowserInterop.Options(_js.Runtime, options, BrowserJsonContext.Default.BrowserTargetOptions);
        return await BrowserInterop.CallAsync(_js, "newTarget", BrowserJsonContext.Default.BrowserTarget,
            JsArg.From(sessionId), url is null ? JsArg.Undefined : JsArg.From(url), BrowserInterop.Optional(init)).ConfigureAwait(false);
    }

    public Task<BrowserTargetActionResult> ActivateTargetAsync(string sessionId, string targetId)
        => BrowserInterop.CallAsync(_js, "activateTarget", BrowserJsonContext.Default.BrowserTargetActionResult,
            JsArg.From(sessionId), JsArg.From(targetId));

    public Task<BrowserTargetActionResult> CloseTargetAsync(string sessionId, string targetId)
        => BrowserInterop.CallAsync(_js, "closeTarget", BrowserJsonContext.Default.BrowserTargetActionResult,
            JsArg.From(sessionId), JsArg.From(targetId));

    public void Dispose() => _js.Dispose();
}
