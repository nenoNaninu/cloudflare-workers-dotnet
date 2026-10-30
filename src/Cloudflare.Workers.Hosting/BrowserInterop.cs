using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Cloudflare.Workers.Hosting.Interop;

namespace Cloudflare.Workers.Hosting;

internal static class BrowserInterop
{
    internal static JsObject? Options<T>(JsRuntime runtime, T? options, JsonTypeInfo<T> typeInfo)
        where T : class
        => options is null ? null : runtime.JsonParse(JsonSerializer.Serialize(options, typeInfo));

    internal static JsObject? AcquireOptions(JsRuntime runtime, BrowserAcquireOptions? options)
    {
        var js = Options(runtime, options, BrowserJsonContext.Default.BrowserAcquireOptions);
        try
        {
            if (options?.OutboundByHost is { } outboundByHost)
            {
                using var outbound = runtime.NewObject();
                foreach (var (host, worker) in outboundByHost)
                {
                    outbound.SetProperty(host, JsArg.From(worker.Js));
                }

                js!.SetProperty("outboundByHost", JsArg.From(outbound));
            }

            return js;
        }
        catch
        {
            js?.Dispose();
            throw;
        }
    }

    internal static async Task<T> CallAsync<T>(JsObject js, string method, JsonTypeInfo<T> typeInfo, params JsArg[] args)
    {
        using var result = await js.CallAsync(method, args).ConfigureAwait(false);
        return Deserialize(result, typeInfo);
    }

    internal static T Deserialize<T>(JsObject js, JsonTypeInfo<T> typeInfo)
        => JsonSerializer.Deserialize(js.Runtime.JsonStringify(js), typeInfo)!;

    internal static JsArg Optional(JsObject? options) => options is null ? JsArg.Undefined : JsArg.From(options);
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(BrowserAcquireOptions))]
[JsonSerializable(typeof(BrowserConnectOptions))]
[JsonSerializable(typeof(BrowserLiveViewOptions))]
[JsonSerializable(typeof(BrowserListOptions))]
[JsonSerializable(typeof(BrowserTargetOptions))]
[JsonSerializable(typeof(BrowserAcquireResult))]
[JsonSerializable(typeof(BrowserSession))]
[JsonSerializable(typeof(BrowserSession[]))]
[JsonSerializable(typeof(BrowserLimits))]
[JsonSerializable(typeof(BrowserTarget))]
[JsonSerializable(typeof(BrowserTarget[]))]
[JsonSerializable(typeof(BrowserVersion))]
[JsonSerializable(typeof(BrowserLiveView))]
[JsonSerializable(typeof(BrowserCloseSessionResult))]
[JsonSerializable(typeof(BrowserTargetActionResult))]
[JsonSerializable(typeof(JsonElement))]
internal partial class BrowserJsonContext : JsonSerializerContext;
