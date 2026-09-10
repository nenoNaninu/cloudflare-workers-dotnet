using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Cloudflare.Workers.Hosting.Interop;

namespace Cloudflare.Workers.Hosting;

public sealed class HttpRequest : IDisposable
{
    private readonly JsObject _js;

    private HttpRequest(JsObject js)
    {
        _js = js;
        Method = js.GetPropertyAsString("method") ?? "GET";
        Url = js.GetPropertyAsString("url") ?? string.Empty;
    }

    internal static HttpRequest FromJsObject(JsObject js)
    {
        try
        {
            return new HttpRequest(js);
        }
        catch
        {
            js.Dispose();
            throw;
        }
    }

    public string Method { get; }

    public string Url { get; }

    public Uri Uri => new(Url);

    public string Path => Uri.AbsolutePath;

    public JsObject Js => _js;

    public HttpHeaders Headers
    {
        get
        {
            if (field is null)
            {
                using var jsHeaders = _js.GetProperty("headers");
                field = HttpHeaders.FromJsObject(jsHeaders);
            }

            return field;
        }
    }

    /// <summary>
    /// https://developers.cloudflare.com/workers/runtime-apis/request/#incomingrequestcfproperties
    /// </summary>
    public string? GetCfPropertyAsString(string name)
    {
        using var cf = _js.GetProperty("cf");
        return cf.IsNullOrUndefined ? null : cf.GetPropertyAsString(name);
    }

    public async Task<string> ReadAsStringAsync()
    {
        using var text = await _js.CallAsync("text").ConfigureAwait(false);
        return text.AsString();
    }

    public async Task<byte[]> ReadAsBytesAsync()
    {
        using var buffer = await _js.CallAsync("arrayBuffer").ConfigureAwait(false);
        return buffer.AsBytes();
    }

    public async Task<T?> ReadAsJsonAsync<T>(JsonTypeInfo<T> typeInfo)
    {
        var bytes = await ReadAsBytesAsync().ConfigureAwait(false);
        return JsonSerializer.Deserialize(bytes, typeInfo);
    }

    public void Dispose() => _js.Dispose();
}
