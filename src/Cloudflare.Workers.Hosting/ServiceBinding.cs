using Cloudflare.Workers.Hosting.Interop;

namespace Cloudflare.Workers.Hosting;

/// <summary>
/// A Cloudflare Service Binding for invoking another Worker.
/// </summary>
public sealed class ServiceBinding : IDisposable
{
    private readonly JsObject _js;

    internal ServiceBinding(JsObject js)
    {
        _js = js;
    }

    public JsObject Js => _js;

    public Task<FetchResponseMessage> FetchAsync(string url)
        => FetchAsync(new FetchRequestMessage(url));

    public async Task<FetchResponseMessage> FetchAsync(FetchRequestMessage request)
    {
        ArgumentNullException.ThrowIfNull(request);

        using var init = request.ToJsObject(_js.Runtime);
        return new FetchResponseMessage(await _js
            .CallAsync("fetch", JsArg.From(request.Url), JsArg.From(init))
            .ConfigureAwait(false));
    }

    public void Dispose() => _js.Dispose();
}
