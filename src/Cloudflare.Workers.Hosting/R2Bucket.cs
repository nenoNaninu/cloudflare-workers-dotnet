using Cloudflare.Workers.Hosting.Interop;

namespace Cloudflare.Workers.Hosting;

public sealed class R2Bucket : IDisposable
{
    private readonly JsObject _js;

    internal R2Bucket(JsObject js)
    {
        _js = js;
    }

    public JsObject Js => _js;

    public async Task<R2Object?> GetAsync(string key)
    {
        var result = await _js.CallAsync("get", JsArg.From(key)).ConfigureAwait(false);
        try
        {
            if (result.IsNullOrUndefined)
            {
                result.Dispose();
                return null;
            }

            return new R2Object(result);
        }
        catch
        {
            result.Dispose();
            throw;
        }
    }

    public async Task<R2Object?> HeadAsync(string key)
    {
        var result = await _js.CallAsync("head", JsArg.From(key)).ConfigureAwait(false);
        try
        {
            if (result.IsNullOrUndefined)
            {
                result.Dispose();
                return null;
            }

            return new R2Object(result);
        }
        catch
        {
            result.Dispose();
            throw;
        }
    }

    public async Task<R2Object> PutAsync(string key, byte[] value)
    {
        var result = await _js.CallAsync("put", JsArg.From(key), JsArg.From(value)).ConfigureAwait(false);
        return new R2Object(result);
    }

    public async Task<R2Object> PutAsync(string key, string value)
    {
        var result = await _js.CallAsync("put", JsArg.From(key), JsArg.From(value)).ConfigureAwait(false);
        return new R2Object(result);
    }

    public async Task DeleteAsync(string key)
    {
        using var result = await _js.CallAsync("delete", JsArg.From(key)).ConfigureAwait(false);
    }

    public void Dispose() => _js.Dispose();
}

public sealed class R2Object : IDisposable
{
    private readonly JsObject _js;

    internal R2Object(JsObject js)
    {
        _js = js;
        try
        {
            Key = js.GetPropertyAsString("key") ?? string.Empty;
            Size = (long)(js.GetPropertyAsNumber("size") ?? 0);
            Etag = js.GetPropertyAsString("etag");
        }
        catch
        {
            js.Dispose();
            throw;
        }
    }

    public string Key { get; }

    public long Size { get; }

    public string? Etag { get; }

    public JsObject Js => _js;

    public async Task<string> BodyTextAsync()
    {
        using var text = await _js.CallAsync("text").ConfigureAwait(false);
        return text.AsString();
    }

    public async Task<byte[]> BodyBytesAsync()
    {
        using var buffer = await _js.CallAsync("arrayBuffer").ConfigureAwait(false);
        return buffer.AsBytes();
    }

    public void Dispose() => _js.Dispose();
}
