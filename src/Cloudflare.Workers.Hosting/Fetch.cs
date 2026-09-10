using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Cloudflare.Workers.Hosting.Interop;

namespace Cloudflare.Workers.Hosting;

public static class Fetch
{
    public static Task<FetchResponseMessage> FetchAsync(string url)
        => FetchAsync(JsRuntime.Current, new FetchRequestMessage(url));

    public static Task<FetchResponseMessage> FetchAsync(FetchRequestMessage message)
        => FetchAsync(JsRuntime.Current, message);

    public static async Task<FetchResponseMessage> FetchAsync(JsRuntime runtime, FetchRequestMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);

        using var fetchFunction = runtime.GetGlobal("fetch");

        if (fetchFunction.Kind != JsValueKind.Function)
        {
            throw new JsException("Global 'fetch' is not available.");
        }

        // https://developers.cloudflare.com/workers/runtime-apis/fetch/
        using var options = message.ToJsObject(runtime);

        using var resultPromise = fetchFunction.InvokeAsFunction(
            null,
            JsArg.From(message.Url),
            JsArg.From(options)
        );

        return new FetchResponseMessage(await runtime.AwaitPromise(resultPromise).ConfigureAwait(false));
    }
}

public sealed class FetchRequestMessage
{
    public FetchRequestMessage(string url)
    {
        ArgumentNullException.ThrowIfNull(url);
        Url = url;
    }

    public string Url { get; }

    public string Method { get; init; } = "GET";

    public HttpHeaders Headers { get; } = new();

    public string? Body { get; init; }

    public byte[]? BytesBody { get; init; }

    public FetchRedirectMode Redirect { get; init; } = FetchRedirectMode.Follow;

    internal JsObject ToJsObject(JsRuntime runtime)
    {
        var init = runtime.NewObject();

        try
        {
            init.SetProperty("method", JsArg.From(Method));
            if (Headers.Count > 0)
            {
                using var pairs = Headers.ToJsObject(runtime);
                init.SetProperty("headers", JsArg.From(pairs));
            }

            if (Body is not null)
            {
                init.SetProperty("body", JsArg.From(Body));
            }
            else if (BytesBody is not null)
            {
                init.SetProperty("body", JsArg.From(BytesBody));
            }

            if (this.Redirect != FetchRedirectMode.Follow)
            {
                var redirect = Redirect switch
                {
                    FetchRedirectMode.Manual => "manual",
                    FetchRedirectMode.Error => "error",
                    _ => throw new InvalidOperationException("The redirect mode must be Follow, Manual, or Error."),
                };

                init.SetProperty("redirect", JsArg.From(redirect));
            }

            return init;
        }
        catch
        {
            init.Dispose();
            throw;
        }
    }
}

public enum FetchRedirectMode
{
    /// <summary>Automatically follows redirects.</summary>
    Follow,
    /// <summary>Returns the redirect response without following it.</summary>
    Manual,
    /// <summary>Fails the fetch operation when a redirect is encountered.</summary>
    Error,
}

public sealed class FetchResponseMessage : IDisposable
{
    private JsObject? _jsResponse;

    internal FetchResponseMessage(JsObject jsResponse)
    {
        _jsResponse = jsResponse;
        StatusCode = (int)(jsResponse.GetPropertyAsNumber("status") ?? 200);
    }

    public int StatusCode { get; set; }

    public HttpHeaders Headers { get; } = new();

    public async Task<string> ReadAsStringAsync()
    {
        ObjectDisposedException.ThrowIf(_jsResponse is null, this);

        using var text = await _jsResponse.CallAsync("text").ConfigureAwait(false);
        return text.AsString();
    }

    public async Task<byte[]> ReadAsBytesAsync()
    {
        ObjectDisposedException.ThrowIf(_jsResponse is null, this);

        using var buffer = await _jsResponse.CallAsync("arrayBuffer").ConfigureAwait(false);
        return buffer.AsBytes();
    }

    public async Task<T?> ReadAsJsonAsync<T>(JsonTypeInfo<T> typeInfo)
    {
        var bytes = await ReadAsBytesAsync().ConfigureAwait(false);
        return JsonSerializer.Deserialize(bytes, typeInfo);
    }

    public HttpHeaders ReadHeaders()
    {
        ObjectDisposedException.ThrowIf(_jsResponse is null, this);

        using var headers = _jsResponse.GetProperty("headers");
        return HttpHeaders.FromJsObject(headers);
    }

    public void Dispose()
    {
        _jsResponse?.Dispose();
        _jsResponse = null;
    }
}
