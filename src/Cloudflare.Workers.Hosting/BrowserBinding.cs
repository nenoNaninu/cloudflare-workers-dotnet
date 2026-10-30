using Cloudflare.Workers.Hosting.Interop;

namespace Cloudflare.Workers.Hosting;

/// <summary>A Cloudflare Browser Run binding for managing browser sessions.</summary>
public sealed class BrowserBinding : IDisposable
{
    private readonly JsObject _js;

    internal BrowserBinding(JsObject js) => _js = js;

    public JsObject Js => _js;

    /// <summary>Returns a DevTools binding wrapper. The caller must dispose it.</summary>
    public BrowserDevTools DevTools => new(_js.GetProperty("devtools"));

    public async Task<BrowserAcquireResult> AcquireAsync(BrowserAcquireOptions? options = null)
    {
        using var init = BrowserInterop.AcquireOptions(_js.Runtime, options);
        return await BrowserInterop.CallAsync(_js, "acquire", BrowserJsonContext.Default.BrowserAcquireResult,
            BrowserInterop.Optional(init)).ConfigureAwait(false);
    }

    public async Task<BrowserConnection> LaunchAsync(BrowserAcquireOptions? options = null)
    {
        using var init = BrowserInterop.AcquireOptions(_js.Runtime, options);
        return new(await _js.CallAsync("launch", BrowserInterop.Optional(init)).ConfigureAwait(false));
    }

    public async Task<BrowserConnection> ConnectSessionAsync(string sessionId, BrowserConnectOptions? options = null)
    {
        using var init = BrowserInterop.Options(_js.Runtime, options, BrowserJsonContext.Default.BrowserConnectOptions);
        return new(await _js.CallAsync("connectSession", JsArg.From(sessionId), BrowserInterop.Optional(init)).ConfigureAwait(false));
    }

    public async Task<BrowserLiveView> GetLiveViewAsync(string sessionId, BrowserLiveViewOptions? options = null)
    {
        using var init = BrowserInterop.Options(_js.Runtime, options, BrowserJsonContext.Default.BrowserLiveViewOptions);
        return await BrowserInterop.CallAsync(_js, "getLiveView", BrowserJsonContext.Default.BrowserLiveView,
            JsArg.From(sessionId), BrowserInterop.Optional(init)).ConfigureAwait(false);
    }

    public async Task<BrowserSession[]> ListSessionsAsync(BrowserListOptions? options = null)
    {
        using var init = BrowserInterop.Options(_js.Runtime, options, BrowserJsonContext.Default.BrowserListOptions);
        return await BrowserInterop.CallAsync(_js, "listSessions", BrowserJsonContext.Default.BrowserSessionArray,
            BrowserInterop.Optional(init)).ConfigureAwait(false);
    }

    public async Task<BrowserSession[]> HistoryAsync(BrowserListOptions? options = null)
    {
        using var init = BrowserInterop.Options(_js.Runtime, options, BrowserJsonContext.Default.BrowserListOptions);
        return await BrowserInterop.CallAsync(_js, "history", BrowserJsonContext.Default.BrowserSessionArray,
            BrowserInterop.Optional(init)).ConfigureAwait(false);
    }

    public Task<BrowserLimits> LimitsAsync()
        => BrowserInterop.CallAsync(_js, "limits", BrowserJsonContext.Default.BrowserLimits);

    public async Task<BrowserSession?> GetSessionAsync(string sessionId)
    {
        using var result = await _js.CallAsync("getSession", JsArg.From(sessionId)).ConfigureAwait(false);
        return result.IsNullOrUndefined ? null : BrowserInterop.Deserialize(result, BrowserJsonContext.Default.BrowserSession);
    }

    public Task<BrowserCloseSessionResult> CloseSessionAsync(string sessionId)
        => BrowserInterop.CallAsync(_js, "closeSession", BrowserJsonContext.Default.BrowserCloseSessionResult, JsArg.From(sessionId));

    /// <summary>Releases the binding handle. Close sessions explicitly with <see cref="CloseSessionAsync"/>.</summary>
    public void Dispose() => _js.Dispose();
}

/// <summary>A session-pinned Fetcher returned by launch or connectSession.</summary>
public sealed class BrowserConnection : IDisposable
{
    private readonly JsObject _js;

    internal BrowserConnection(JsObject js)
    {
        _js = js;
        try
        {
            SessionId = js.GetPropertyAsString("sessionId") ?? string.Empty;
            using var targets = js.GetProperty("targets");
            Targets = targets.IsNullOrUndefined ? null : BrowserInterop.Deserialize(targets, BrowserJsonContext.Default.BrowserTargetArray);
            WebSocket = new ServiceBinding(js.GetProperty("webSocket"));
        }
        catch
        {
            js.Dispose();
            throw;
        }
    }

    public JsObject Js => _js;
    public string SessionId { get; }
    public BrowserTarget[]? Targets { get; }
    public ServiceBinding WebSocket { get; }

    /// <summary>Releases connection handles without closing the browser session.</summary>
    public void Dispose()
    {
        WebSocket.Dispose();
        _js.Dispose();
    }
}
