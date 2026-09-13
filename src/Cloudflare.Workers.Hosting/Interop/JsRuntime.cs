namespace Cloudflare.Workers.Hosting.Interop;

public sealed class JsRuntime
{
    private JsObject? _undefined;
    private JsObject? _null;

    public JsRuntime(IJsInterop interop)
    {
        Interop = interop;
    }

    public IJsInterop Interop { get; }

    public static JsRuntime Current
    {
        get => field ??= new JsRuntime(new WasmJsInterop());
        private set;
    }

    public static void SetCurrent(JsRuntime runtime) => Current = runtime;

    public JsObject Undefined => _undefined ??= new JsObject(this, JsHandle.Undefined);

    public JsObject Null => _null ??= new JsObject(this, JsHandle.Null);

    public JsObject WrapToJsObject(JsHandle handle) => new(this, handle);

    public JsObject GetGlobal(string name) => WrapToJsObject(Interop.GetGlobal(name));

    public JsObject NewObject() => WrapToJsObject(Interop.ObjectNew());

    public JsObject NewArray() => WrapToJsObject(Interop.ArrayNew());

    public JsObject NewArray(params JsArg[] items)
    {
        var array = Interop.ArrayNew();
        try
        {
            foreach (var item in items)
            {
                var handle = item.CreateHandle(Interop);
                try
                {
                    Interop.ArrayPush(array, handle);
                }
                finally
                {
                    Interop.ReleaseHandle(handle);
                }
            }

            return WrapToJsObject(array);
        }
        catch
        {
            Interop.ReleaseHandle(array);
            throw;
        }
    }

    public JsObject Construct(string constructorName, params JsArg[] args)
    {
        using var constructor = GetGlobal(constructorName);
        if (constructor.IsNullOrUndefined)
        {
            throw new JsException($"Global constructor '{constructorName}' was not found.");
        }

        var argsArray = Interop.ArrayNew();
        try
        {
            foreach (var arg in args)
            {
                var handle = arg.CreateHandle(Interop);
                try
                {
                    Interop.ArrayPush(argsArray, handle);
                }
                finally
                {
                    Interop.ReleaseHandle(handle);
                }
            }

            if (!Interop.TryConstruct(constructor.Handle, argsArray, out var result))
            {
                throw CreateException(result);
            }

            return WrapToJsObject(result);
        }
        finally
        {
            Interop.ReleaseHandle(argsArray);
        }
    }

    public Task<JsObject> AwaitPromise(JsObject promise)
    {
        var tcs = new TaskCompletionSource<JsObject>();

        int continuationId = ContinuationRegistry.Register((success, handle) =>
        {
            var value = WrapToJsObject(handle);
            if (success)
            {
                tcs.SetResult(value);
            }
            else
            {
                string message = DescribeError(value);
                value.Dispose();
                tcs.SetException(new JsException(message));
            }
        });

        Interop.PromiseRegisterContinuation(promise.Handle, continuationId);
        return tcs.Task;
    }

    public string JsonStringify(JsObject value)
    {
        using var json = GetGlobal("JSON");
        using var result = json.Call("stringify", JsArg.From(value));
        return result.IsNullOrUndefined ? "null" : result.AsString();
    }

    public JsObject JsonParse(string json)
    {
        using var jsonGlobal = GetGlobal("JSON");
        return jsonGlobal.Call("parse", JsArg.From(json));
    }

    public string DescribeError(JsObject error)
    {
        try
        {
            switch (error.Kind)
            {
                case JsValueKind.String:
                    return error.AsString();
                case JsValueKind.Object:
                {
                    string? stack = error.GetPropertyAsString("stack");
                    if (!string.IsNullOrEmpty(stack))
                    {
                        return stack;
                    }

                    string? message = error.GetPropertyAsString("message");
                    if (!string.IsNullOrEmpty(message))
                    {
                        return message;
                    }

                    return JsonStringify(error);
                }
                default:
                    return $"JavaScript error ({error.Kind})";
            }
        }
        catch
        {
            return "JavaScript error (unreadable)";
        }
    }

    internal JsException CreateException(JsHandle errorHandle)
    {
        using var error = WrapToJsObject(errorHandle);
        return new JsException(DescribeError(error));
    }
}
