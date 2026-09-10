using System.Text;
using Cloudflare.Workers.Hosting.Interop;

namespace Cloudflare.Workers.Hosting.Tests.Fakes;

/// <summary>Builders for the standard globals and Workers objects the library talks to.</summary>
public static class FakeWorld
{
    public static (FakeJsInterop Interop, JsRuntime Runtime) Create()
    {
        var interop = new FakeJsInterop();
        InstallStandardGlobals(interop);
        return (interop, new JsRuntime(interop));
    }

    public static void InstallStandardGlobals(FakeJsInterop interop)
    {
        interop.InstallJsonGlobal();

        interop.Globals["Array"] = new FakeObject
        {
            // Array.from — the library only ever passes arrays or array-shaped iterators.
            ["from"] = new FakeFunction((_, args) => args[0] switch
            {
                FakeArray array => array,
                _ => throw FakeJsThrow.WithMessage("Array.from: unsupported input"),
            }),
        };

        interop.Globals["Response"] = new FakeConstructor(args => new FakeObject
        {
            ["__type"] = "Response",
            ["body"] = args.ElementAtOrDefault(0),
            ["status"] = (args.ElementAtOrDefault(1) as FakeObject)?.GetValueOrDefault("status") ?? 200d,
            ["statusText"] = (args.ElementAtOrDefault(1) as FakeObject)?.GetValueOrDefault("statusText"),
            ["headers"] = (args.ElementAtOrDefault(1) as FakeObject)?.GetValueOrDefault("headers"),
        });

        interop.Globals["Uint8Array"] = new FakeConstructor(args => args.ElementAtOrDefault(0) switch
        {
            byte[] bytes => bytes,
            double length => new byte[(int)length],
            _ => Array.Empty<byte>(),
        });
    }

    // -- Workers objects ---------------------------------------------------------

    public static FakeObject CreateHeaders(params (string Name, string Value)[] pairs)
    {
        var entries = new FakeArray(pairs.Select(p => (object?)new FakeArray([p.Name, p.Value])));
        return new FakeObject
        {
            ["entries"] = new FakeFunction((_, _) => entries),
        };
    }

    public static FakeObject CreateRequest(
        string method,
        string url,
        (string Name, string Value)[]? headers = null,
        string? body = null,
        FakeObject? cf = null)
    {
        var request = new FakeObject
        {
            ["method"] = method,
            ["url"] = url,
            ["headers"] = CreateHeaders(headers ?? []),
            ["text"] = new FakeFunction((_, _) => FakePromise.Resolved(body ?? string.Empty)),
            ["arrayBuffer"] = new FakeFunction((_, _) => FakePromise.Resolved(Encoding.UTF8.GetBytes(body ?? string.Empty))),
        };
        if (cf is not null)
        {
            request["cf"] = cf;
        }

        return request;
    }

    /// <summary>An in-memory KV namespace; values are strings or byte arrays.</summary>
    public static FakeObject CreateKv(Dictionary<string, object?> store)
    {
        return new FakeObject
        {
            ["get"] = new FakeFunction((_, args) =>
            {
                string key = (string)args[0]!;
                if (!store.TryGetValue(key, out var value))
                {
                    return FakePromise.Resolved(null);
                }

                bool wantsBytes = (args.ElementAtOrDefault(1) as FakeObject)?.GetValueOrDefault("type") as string == "arrayBuffer";
                return FakePromise.Resolved(wantsBytes && value is string text ? Encoding.UTF8.GetBytes(text) : value);
            }),
            ["put"] = new FakeFunction((_, args) =>
            {
                store[(string)args[0]!] = args[1];
                store["__lastPutOptions"] = args.ElementAtOrDefault(2);
                return FakePromise.Resolved(JsUndefined.Value);
            }),
            ["delete"] = new FakeFunction((_, args) =>
            {
                store.Remove((string)args[0]!);
                return FakePromise.Resolved(JsUndefined.Value);
            }),
            ["list"] = new FakeFunction((_, args) =>
            {
                string prefix = (args.ElementAtOrDefault(0) as FakeObject)?.GetValueOrDefault("prefix") as string ?? string.Empty;
                var keys = new FakeArray(store.Keys
                    .Where(k => !k.StartsWith("__") && k.StartsWith(prefix, StringComparison.Ordinal))
                    .OrderBy(k => k, StringComparer.Ordinal)
                    .Select(k => (object?)new FakeObject { ["name"] = k }));
                return FakePromise.Resolved(new FakeObject
                {
                    ["keys"] = keys,
                    ["list_complete"] = true,
                });
            }),
        };
    }

    public static Env CreateEnv(JsRuntime runtime, FakeJsInterop interop, FakeObject? bindings = null)
        => new(runtime.WrapToJsObject(interop.Retain(bindings ?? [])));

    public static WorkerContext CreateContext(JsRuntime runtime, FakeJsInterop interop, FakeObject? ctx = null)
        => new(runtime.WrapToJsObject(interop.Retain(ctx ?? [])));
}
