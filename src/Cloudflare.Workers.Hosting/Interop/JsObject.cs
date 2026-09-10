namespace Cloudflare.Workers.Hosting.Interop;

public sealed class JsObject : IDisposable
{
    private int _disposed;
    private JsValueKind? _kind;

    public JsObject(JsRuntime runtime, JsHandle handle)
    {
        Runtime = runtime;
        Handle = handle;
    }

    public JsRuntime Runtime { get; }

    public JsHandle Handle { get; }

    public JsValueKind Kind => _kind ??= Runtime.Interop.GetKind(Handle);

    public bool IsNullOrUndefined => Kind is JsValueKind.Undefined or JsValueKind.Null;

    public string AsString() => Runtime.Interop.StringGet(Handle);

    public double AsNumber() => Runtime.Interop.NumberValue(Handle);

    public bool AsBoolean() => Runtime.Interop.BooleanValue(Handle);

    public byte[] AsBytes()
    {
        int length = Runtime.Interop.BytesLength(Handle);
        if (length == 0)
        {
            return [];
        }

        var bytes = new byte[length];
        Runtime.Interop.BytesRead(Handle, bytes);
        return bytes;
    }

    public JsObject GetProperty(string name) => Runtime.WrapToJsObject(Runtime.Interop.GetProperty(Handle, name));

    public string? GetPropertyAsString(string name)
    {
        using var value = GetProperty(name);
        return value.IsNullOrUndefined ? null : value.AsString();
    }

    public double? GetPropertyAsNumber(string name)
    {
        using var value = GetProperty(name);
        return value.IsNullOrUndefined ? null : value.AsNumber();
    }

    public bool? GetPropertyAsBoolean(string name)
    {
        using var value = GetProperty(name);
        return value.IsNullOrUndefined ? null : value.AsBoolean();
    }

    public void SetProperty(string name, JsArg value)
    {
        JsHandle? handle = null;

        try
        {
            handle = value.CreateHandle(Runtime.Interop);
            Runtime.Interop.SetProperty(Handle, name, handle.Value);
        }
        finally
        {
            if (handle.HasValue)
            {
                Runtime.Interop.ReleaseHandle(handle.Value);
            }
        }
    }

    public int GetArrayLength() => Runtime.Interop.ArrayLength(Handle);

    public JsObject GetElement(int index) => Runtime.WrapToJsObject(Runtime.Interop.ArrayGet(Handle, index));

    public JsObject Call(string method, params JsArg[] args)
    {
        var interop = Runtime.Interop;
        var argsArray = interop.ArrayNew();
        try
        {
            foreach (var arg in args)
            {
                var handle = arg.CreateHandle(interop);
                try
                {
                    interop.ArrayPush(argsArray, handle);
                }
                finally
                {
                    interop.ReleaseHandle(handle);
                }
            }

            if (!interop.TryCallMethod(Handle, method, argsArray, out var result))
            {
                throw Runtime.CreateException(result);
            }

            return Runtime.WrapToJsObject(result);
        }
        finally
        {
            interop.ReleaseHandle(argsArray);
        }
    }

    public async Task<JsObject> CallAsync(string method, params JsArg[] args)
    {
        using var promise = Call(method, args);
        return await Runtime.AwaitPromise(promise).ConfigureAwait(false);
    }

    public JsObject InvokeAsFunction(JsObject? thisArg, params JsArg[] args)
    {
        var interop = Runtime.Interop;
        var argsArray = interop.ArrayNew();
        try
        {
            foreach (var arg in args)
            {
                var handle = arg.CreateHandle(interop);
                try
                {
                    interop.ArrayPush(argsArray, handle);
                }
                finally
                {
                    interop.ReleaseHandle(handle);
                }
            }

            var thisHandle = thisArg?.Handle ?? JsHandle.Undefined;
            if (!interop.TryCallFunction(Handle, thisHandle, argsArray, out var result))
            {
                throw Runtime.CreateException(result);
            }

            return Runtime.WrapToJsObject(result);
        }
        finally
        {
            interop.ReleaseHandle(argsArray);
        }
    }

    public JsObject Clone() => Runtime.WrapToJsObject(Runtime.Interop.Clone(Handle));

    internal JsHandle MoveHandleOwnership()
    {
        ObjectDisposedException.ThrowIf(
            Interlocked.CompareExchange(ref _disposed, 1, 0) != 0,
            nameof(JsObject)
        );

        return Handle;
    }

    public void Dispose() => ReleaseHandle();

    private void ReleaseHandle()
    {
        if (Interlocked.CompareExchange(ref _disposed, 1, 0) == 0 && !Handle.IsReserved)
        {
            Runtime.Interop.ReleaseHandle(Handle);
        }
    }
}
