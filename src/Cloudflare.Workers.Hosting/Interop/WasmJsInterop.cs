using System.Buffers;
using System.Text;

namespace Cloudflare.Workers.Hosting.Interop;

public sealed unsafe class WasmJsInterop : IJsInterop
{
    private const int StackBufferSize = 512;

    public JsHandle StringNew(string value)
    {
        if (value.Length == 0)
        {
            return new JsHandle(WasmImports.StringNew(null, 0));
        }

        int byteCount = Encoding.UTF8.GetByteCount(value);
        if (byteCount <= StackBufferSize)
        {
            Span<byte> buffer = stackalloc byte[StackBufferSize];
            Encoding.UTF8.GetBytes(value, buffer);
            fixed (byte* ptr = buffer)
            {
                return new JsHandle(WasmImports.StringNew(ptr, byteCount));
            }
        }

        byte[] rented = ArrayPool<byte>.Shared.Rent(byteCount);
        try
        {
            Encoding.UTF8.GetBytes(value, rented);
            fixed (byte* ptr = rented)
            {
                return new JsHandle(WasmImports.StringNew(ptr, byteCount));
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    public string StringGet(JsHandle handle)
    {
        int byteCount = WasmImports.StringUtf8Length(handle.Value);
        if (byteCount == 0)
        {
            return string.Empty;
        }

        byte[] rented = ArrayPool<byte>.Shared.Rent(byteCount);
        try
        {
            fixed (byte* ptr = rented)
            {
                WasmImports.StringRead(handle.Value, ptr);
            }

            return Encoding.UTF8.GetString(rented, 0, byteCount);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    public JsHandle NumberNew(double value) => new(WasmImports.NumberNew(value));

    public double NumberValue(JsHandle handle) => WasmImports.NumberValue(handle.Value);

    public JsHandle BooleanNew(bool value) => new(WasmImports.BooleanNew(value ? 1 : 0));

    public bool BooleanValue(JsHandle handle) => WasmImports.BooleanValue(handle.Value) != 0;

    public JsValueKind GetKind(JsHandle handle) => (JsValueKind)WasmImports.ValueKind(handle.Value);

    public JsHandle Clone(JsHandle handle) => new(WasmImports.Clone(handle.Value));

    public void ReleaseHandle(JsHandle handle)
    {
        if (!handle.IsReserved)
        {
            WasmImports.ReleaseHandle(handle.Value);
        }
    }

    public JsHandle ObjectNew() => new(WasmImports.ObjectNew());

    public JsHandle ArrayNew() => new(WasmImports.ArrayNew());

    public int ArrayLength(JsHandle array) => WasmImports.ArrayLength(array.Value);

    public JsHandle ArrayGet(JsHandle array, int index) => new(WasmImports.ArrayGet(array.Value, index));

    public void ArrayPush(JsHandle array, JsHandle handle) => WasmImports.ArrayPush(array.Value, handle.Value);

    public JsHandle GetGlobal(string name)
    {
        fixed (byte* ptr = Encoding.UTF8.GetBytes(name))
        {
            return new JsHandle(WasmImports.GlobalGet(ptr, Encoding.UTF8.GetByteCount(name)));
        }
    }

    public JsHandle GetProperty(JsHandle target, string name)
    {
        byte[] utf8 = Encoding.UTF8.GetBytes(name);
        fixed (byte* ptr = utf8)
        {
            return new JsHandle(WasmImports.PropertyGet(target.Value, ptr, utf8.Length));
        }
    }

    public void SetProperty(JsHandle target, string name, JsHandle handle)
    {
        byte[] utf8 = Encoding.UTF8.GetBytes(name);
        fixed (byte* ptr = utf8)
        {
            WasmImports.PropertySet(target.Value, ptr, utf8.Length, handle.Value);
        }
    }

    public bool TryCallMethod(JsHandle target, string name, JsHandle argsArray, out JsHandle result)
    {
        byte[] utf8 = Encoding.UTF8.GetBytes(name);
        int resultHandle;
        int status;
        fixed (byte* ptr = utf8)
        {
            status = WasmImports.CallMethod(target.Value, ptr, utf8.Length, argsArray.Value, &resultHandle);
        }

        result = new JsHandle(resultHandle);
        return status == 0;
    }

    public bool TryCallFunction(JsHandle function, JsHandle thisArg, JsHandle argsArray, out JsHandle result)
    {
        int resultHandle;
        int status = WasmImports.CallFunction(function.Value, thisArg.Value, argsArray.Value, &resultHandle);
        result = new JsHandle(resultHandle);
        return status == 0;
    }

    public bool TryConstruct(JsHandle constructor, JsHandle argsArray, out JsHandle result)
    {
        int resultHandle;
        int status = WasmImports.Construct(constructor.Value, argsArray.Value, &resultHandle);
        result = new JsHandle(resultHandle);
        return status == 0;
    }

    public JsHandle BytesNew(ReadOnlySpan<byte> bytes)
    {
        fixed (byte* ptr = bytes)
        {
            return new JsHandle(WasmImports.BytesNew(ptr, bytes.Length));
        }
    }

    public int BytesLength(JsHandle handle) => WasmImports.BytesLength(handle.Value);

    public void BytesRead(JsHandle handle, Span<byte> destination)
    {
        fixed (byte* ptr = destination)
        {
            WasmImports.BytesRead(handle.Value, ptr);
        }
    }

    public void PromiseRegisterCallback(JsHandle promise, int callbackId)
        => WasmImports.PromiseRegisterCallback(promise.Value, callbackId);

    public PromiseId PromiseNew() => new(WasmImports.PromiseNew());

    public JsHandle PromiseGet(PromiseId promiseId) => new(WasmImports.PromiseGet(promiseId.Value));

    public void PromiseResolve(PromiseId promiseId, JsHandle handle)
        => WasmImports.PromiseResolve(promiseId.Value, handle.Value);

    public void PromiseReject(PromiseId promiseId, string message)
    {
        byte[] utf8 = Encoding.UTF8.GetBytes(message);
        fixed (byte* ptr = utf8)
        {
            WasmImports.PromiseReject(promiseId.Value, ptr, utf8.Length);
        }
    }

    public void SetTimeout(int callbackId, double milliseconds)
        => WasmImports.SetTimeout(callbackId, milliseconds);

    public void Log(ConsoleLevel level, string message)
    {
        byte[] utf8 = Encoding.UTF8.GetBytes(message);
        fixed (byte* ptr = utf8)
        {
            WasmImports.Log((int)level, ptr, utf8.Length);
        }
    }
}
