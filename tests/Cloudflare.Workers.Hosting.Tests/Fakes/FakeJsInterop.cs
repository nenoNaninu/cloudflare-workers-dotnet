using System.Text.Json;
using System.Text.Json.Nodes;
using Cloudflare.Workers.Hosting.Interop;

namespace Cloudflare.Workers.Hosting.Tests.Fakes;

/// <summary>
/// An in-process implementation of <see cref="IJsInterop"/> that emulates the JS
/// shim's handle table over a tiny managed "JS world", so the library can be
/// exercised on regular .NET without a wasm runtime.
/// </summary>
public sealed class FakeJsInterop : IJsInterop
{
    private readonly List<object?> _heap = [JsUndefined.Value, null, true, false];
    private readonly Stack<int> _freeSlots = new();
    private readonly Dictionary<int, byte[]> _encodedStrings = [];
    private int _nextPromiseId;

    public FakeObject Globals { get; } = [];

    public List<(ConsoleLevel Level, string Message)> Logs { get; } = [];

    /// <summary>Promises created via <see cref="PromiseNew"/>, kept for assertions.</summary>
    public Dictionary<PromiseId, FakePromise> Promises { get; } = [];

    /// <summary>When false, timeouts queue in <see cref="PendingTimeouts"/> until <see cref="FireTimeouts"/>.</summary>
    public bool AutoFireTimeouts { get; set; } = true;

    public List<int> PendingTimeouts { get; } = [];

    // -- test helpers --------------------------------------------------------

    public JsHandle Retain(object? value)
    {
        if (_freeSlots.Count > 0)
        {
            int slot = _freeSlots.Pop();
            _heap[slot] = value;
            return new JsHandle(slot);
        }

        _heap.Add(value);
        return new JsHandle(_heap.Count - 1);
    }

    public object? Get(JsHandle handle) => _heap[handle.Value];

    public int LiveHandleCount => _heap.Count - 4 - _freeSlots.Count;

    public void FireTimeouts()
    {
        var pending = PendingTimeouts.ToArray();
        PendingTimeouts.Clear();
        foreach (int continuationId in pending)
        {
            ContinuationRegistry.Complete(continuationId, true, JsHandle.Undefined);
        }
    }

    // -- IJsInterop -----------------------------------------------------------

    public JsHandle StringNew(string value) => Retain(value);

    public string StringGet(JsHandle handle)
        => Get(handle) as string ?? throw new InvalidOperationException($"Handle {handle} is not a string but {Describe(handle)}.");

    public JsHandle NumberNew(double value) => Retain(value);

    public double NumberValue(JsHandle handle)
        => Get(handle) is double d ? d : throw new InvalidOperationException($"Handle {handle} is not a number but {Describe(handle)}.");

    public JsHandle BooleanNew(bool value) => value ? JsHandle.True : JsHandle.False;

    public bool BooleanValue(JsHandle handle) => Get(handle) switch
    {
        bool b => b,
        var other => throw new InvalidOperationException($"Handle {handle} is not a boolean but {other?.GetType().Name}."),
    };

    public JsValueKind GetKind(JsHandle handle) => Get(handle) switch
    {
        JsUndefined => JsValueKind.Undefined,
        null => JsValueKind.Null,
        bool => JsValueKind.Boolean,
        double => JsValueKind.Number,
        string => JsValueKind.String,
        FakeFunction or FakeConstructor => JsValueKind.Function,
        _ => JsValueKind.Object,
    };

    public JsHandle Clone(JsHandle handle) => Retain(Get(handle));

    public void ReleaseHandle(JsHandle handle)
    {
        if (handle.IsReserved)
        {
            return;
        }

        _heap[handle.Value] = JsUndefined.Value;
        _freeSlots.Push(handle.Value);
        _encodedStrings.Remove(handle.Value);
    }

    public JsHandle ObjectNew() => Retain(new FakeObject());

    public JsHandle ArrayNew() => Retain(new FakeArray());

    public int ArrayLength(JsHandle array) => AsArray(array).Count;

    public JsHandle ArrayGet(JsHandle array, int index) => Retain(AsArray(array)[index]);

    public void ArrayPush(JsHandle array, JsHandle handle) => AsArray(array).Add(Get(handle));

    public JsHandle GetGlobal(string name)
        => Retain(Globals.TryGetValue(name, out var value) ? value : JsUndefined.Value);

    public JsHandle GetProperty(JsHandle target, string name)
    {
        return Get(target) switch
        {
            FakeObject obj when obj.TryGetValue(name, out var value) => Retain(value),
            _ => JsHandle.Undefined,
        };
    }

    public void SetProperty(JsHandle target, string name, JsHandle handle)
    {
        if (Get(target) is not FakeObject obj)
        {
            throw new InvalidOperationException($"Handle {target} is not an object.");
        }

        obj[name] = Get(handle);
    }

    public bool TryCallMethod(JsHandle target, string name, JsHandle argsArray, out JsHandle result)
    {
        object?[] args = [.. AsArray(argsArray)];
        try
        {
            var receiver = Get(target);
            if (receiver is not FakeObject obj || !obj.TryGetValue(name, out var member) || member is not FakeFunction function)
            {
                throw FakeJsThrow.WithMessage($"{name} is not a function");
            }

            result = Retain(function.Invoke(receiver, args));
            return true;
        }
        catch (FakeJsThrow thrown)
        {
            result = Retain(thrown.Error);
            return false;
        }
    }

    public bool TryCallFunction(JsHandle function, JsHandle thisArg, JsHandle argsArray, out JsHandle result)
    {
        object?[] args = [.. AsArray(argsArray)];
        try
        {
            if (Get(function) is not FakeFunction fn)
            {
                throw FakeJsThrow.WithMessage("value is not a function");
            }

            result = Retain(fn.Invoke(Get(thisArg), args));
            return true;
        }
        catch (FakeJsThrow thrown)
        {
            result = Retain(thrown.Error);
            return false;
        }
    }

    public bool TryConstruct(JsHandle constructor, JsHandle argsArray, out JsHandle result)
    {
        object?[] args = [.. AsArray(argsArray)];
        try
        {
            if (Get(constructor) is not FakeConstructor ctor)
            {
                throw FakeJsThrow.WithMessage("value is not a constructor");
            }

            result = Retain(ctor.Construct(args));
            return true;
        }
        catch (FakeJsThrow thrown)
        {
            result = Retain(thrown.Error);
            return false;
        }
    }

    public JsHandle BytesNew(ReadOnlySpan<byte> bytes) => Retain(bytes.ToArray());

    public int BytesLength(JsHandle handle) => AsBytes(handle).Length;

    public void BytesRead(JsHandle handle, Span<byte> destination) => AsBytes(handle).CopyTo(destination);

    public void PromiseRegisterContinuation(JsHandle promise, int continuationId)
    {
        var value = Get(promise);
        if (value is FakePromise fakePromise)
        {
            fakePromise.OnSettled((ok, result) => ContinuationRegistry.Complete(continuationId, ok, Retain(result)));
        }
        else
        {
            // Promise.resolve semantics: non-promises settle immediately.
            ContinuationRegistry.Complete(continuationId, true, Retain(value));
        }
    }

    public PromiseId PromiseNew()
    {
        var id = new PromiseId(++_nextPromiseId);
        Promises.Add(id, new FakePromise());
        return id;
    }

    public JsHandle PromiseGet(PromiseId promiseId) => Retain(Promises[promiseId]);

    public void PromiseResolve(PromiseId promiseId, JsHandle handle)
    {
        Promises[promiseId].Resolve(Get(handle));
        ReleaseHandle(handle);
    }

    public void PromiseReject(PromiseId promiseId, string message)
        => Promises[promiseId].Reject(new FakeObject { ["message"] = message });

    public void SetTimeout(int continuationId, double milliseconds)
    {
        if (AutoFireTimeouts)
        {
            ContinuationRegistry.Complete(continuationId, true, JsHandle.Undefined);
        }
        else
        {
            PendingTimeouts.Add(continuationId);
        }
    }

    public void Log(ConsoleLevel level, string message) => Logs.Add((level, message));

    // -- JSON global (used by JsRuntime.JsonStringify/JsonParse) ---------------

    public void InstallJsonGlobal()
    {
        Globals["JSON"] = new FakeObject
        {
            ["stringify"] = new FakeFunction((_, args) => ToJsonNode(args.ElementAtOrDefault(0))?.ToJsonString() ?? "null"),
            ["parse"] = new FakeFunction((_, args) => FromJsonNode(JsonNode.Parse((string)args[0]!))),
        };
    }

    private static JsonNode? ToJsonNode(object? value) => value switch
    {
        null or JsUndefined => null,
        bool b => JsonValue.Create(b),
        double d => JsonValue.Create(d),
        string s => JsonValue.Create(s),
        byte[] bytes => new JsonArray([.. bytes.Select(b => (JsonNode?)JsonValue.Create((double)b))]),
        FakeArray array => new JsonArray([.. array.Select(ToJsonNode)]),
        FakeObject obj => new JsonObject(obj
            .Where(p => p.Value is not (FakeFunction or FakeConstructor or JsUndefined))
            .Select(p => KeyValuePair.Create(p.Key, ToJsonNode(p.Value)))),
        _ => new JsonObject(),
    };

    private static object? FromJsonNode(JsonNode? node) => node switch
    {
        null => null,
        JsonArray array => new FakeArray(array.Select(FromJsonNode)),
        JsonObject obj => CreateObject(obj),
        JsonValue value => value.GetValueKind() switch
        {
            JsonValueKind.String => value.GetValue<string>(),
            JsonValueKind.Number => value.GetValue<double>(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null,
        },
        _ => null,
    };

    private static FakeObject CreateObject(JsonObject obj)
    {
        var result = new FakeObject();
        foreach (var (key, value) in obj)
        {
            result[key] = FromJsonNode(value);
        }

        return result;
    }

    private FakeArray AsArray(JsHandle handle)
        => Get(handle) as FakeArray ?? throw new InvalidOperationException($"Handle {handle} is not an array but {Describe(handle)}.");

    private byte[] AsBytes(JsHandle handle)
        => Get(handle) as byte[] ?? throw new InvalidOperationException($"Handle {handle} is not binary data but {Describe(handle)}.");

    private string Describe(JsHandle handle) => Get(handle)?.GetType().Name ?? "null";
}
