using Cloudflare.Workers.Hosting.Interop;

namespace Cloudflare.Workers.Hosting;

public sealed class KvNamespace : IDisposable
{
    private readonly JsObject _js;

    internal KvNamespace(JsObject js)
    {
        _js = js;
    }

    public JsObject Js => _js;

    public async Task<string?> GetTextAsync(string key)
    {
        using var value = await _js.CallAsync("get", JsArg.From(key)).ConfigureAwait(false);
        return value.IsNullOrUndefined ? null : value.AsString();
    }

    public async Task<byte[]?> GetBytesAsync(string key)
    {
        using var options = _js.Runtime.NewObject();
        options.SetProperty("type", JsArg.From("arrayBuffer"));
        using var value = await _js.CallAsync("get", JsArg.From(key), JsArg.From(options)).ConfigureAwait(false);
        return value.IsNullOrUndefined ? null : value.AsBytes();
    }

    public Task PutAsync(string key, string value, KvPutOptions? options = null)
        => PutCoreAsync(key, JsArg.From(value), options);

    public Task PutAsync(string key, byte[] value, KvPutOptions? options = null)
        => PutCoreAsync(key, JsArg.From(value), options);

    public async Task DeleteAsync(string key)
    {
        using var result = await _js.CallAsync("delete", JsArg.From(key)).ConfigureAwait(false);
    }

    public async Task<KvListResult> ListAsync(KvListOptions? options = null)
    {
        using var jsOptions = _js.Runtime.NewObject();
        if (options?.Prefix is not null)
        {
            jsOptions.SetProperty("prefix", JsArg.From(options.Prefix));
        }

        if (options?.Limit is not null)
        {
            jsOptions.SetProperty("limit", JsArg.From(options.Limit.Value));
        }

        if (options?.Cursor is not null)
        {
            jsOptions.SetProperty("cursor", JsArg.From(options.Cursor));
        }

        using var result = await _js.CallAsync("list", JsArg.From(jsOptions)).ConfigureAwait(false);

        var keys = new List<KvKey>();
        using (var jsKeys = result.GetProperty("keys"))
        {
            int count = jsKeys.GetArrayLength();
            for (int i = 0; i < count; i++)
            {
                using var jsKey = jsKeys.GetElement(i);
                double? expiration = jsKey.GetPropertyAsNumber("expiration");
                keys.Add(new KvKey(
                    jsKey.GetPropertyAsString("name") ?? string.Empty,
                    expiration is null ? null : DateTimeOffset.FromUnixTimeSeconds((long)expiration.Value)));
            }
        }

        bool complete = result.GetPropertyAsBoolean("list_complete") ?? true;
        string? cursor = complete ? null : result.GetPropertyAsString("cursor");
        return new KvListResult(keys, complete, cursor);
    }

    public void Dispose() => _js.Dispose();

    private async Task PutCoreAsync(string key, JsArg value, KvPutOptions? options)
    {
        if (options is null)
        {
            using var result = await _js.CallAsync("put", JsArg.From(key), value).ConfigureAwait(false);
            return;
        }

        using var jsOptions = _js.Runtime.NewObject();
        if (options.ExpirationTtl is not null)
        {
            jsOptions.SetProperty("expirationTtl", JsArg.From(options.ExpirationTtl.Value.TotalSeconds));
        }

        if (options.Expiration is not null)
        {
            jsOptions.SetProperty("expiration", JsArg.From(options.Expiration.Value.ToUnixTimeSeconds()));
        }

        using var putResult = await _js.CallAsync("put", JsArg.From(key), value, JsArg.From(jsOptions)).ConfigureAwait(false);
    }
}

public sealed class KvPutOptions
{
    public TimeSpan? ExpirationTtl { get; init; }

    public DateTimeOffset? Expiration { get; init; }
}

public sealed class KvListOptions
{
    public string? Prefix { get; init; }

    public int? Limit { get; init; }

    public string? Cursor { get; init; }
}

public readonly record struct KvKey(string Name, DateTimeOffset? Expiration);

public sealed record KvListResult(IReadOnlyList<KvKey> Keys, bool ListComplete, string? Cursor);
