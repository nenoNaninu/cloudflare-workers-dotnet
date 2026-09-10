namespace Cloudflare.Workers.Hosting.Interop;

public readonly struct JsArg
{
    private enum ArgKind
    {
        Undefined,
        Null,
        String,
        Number,
        Boolean,
        Bytes,
        Object,
    }

    private readonly ArgKind _kind;
    private readonly string? _string;
    private readonly double _number;
    private readonly bool _boolean;
    private readonly byte[]? _bytes;
    private readonly JsObject? _object;

    private JsArg(ArgKind kind, string? s = null, double number = 0, bool boolean = false, byte[]? bytes = null, JsObject? obj = null)
    {
        _kind = kind;
        _string = s;
        _number = number;
        _boolean = boolean;
        _bytes = bytes;
        _object = obj;
    }

    public static JsArg Undefined => new(ArgKind.Undefined);

    public static JsArg Null => new(ArgKind.Null);

    public static JsArg From(string? value)
        => value is null ? Null : new JsArg(ArgKind.String, s: value);

    public static JsArg From(double value)
        => new(ArgKind.Number, number: value);

    public static JsArg From(int value)
        => new(ArgKind.Number, number: value);

    public static JsArg From(long value)
        => new(ArgKind.Number, number: value);

    public static JsArg From(bool value)
        => new(ArgKind.Boolean, boolean: value);

    public static JsArg From(byte[]? value)
        => value is null ? Null : new JsArg(ArgKind.Bytes, bytes: value);

    public static JsArg From(JsObject? value)
        => value is null ? Null : new JsArg(ArgKind.Object, obj: value);

    /// <summary>
    /// Creates a handle for this argument. The caller must release the returned handle
    /// after the JavaScript bridge has consumed it. Existing <see cref="JsObject"/>
    /// handles are cloned so the returned handle always follows that ownership rule.
    /// </summary>
    internal JsHandle CreateHandle(IJsInterop interop)
    {
        switch (_kind)
        {
            case ArgKind.Undefined:
                return JsHandle.Undefined;
            case ArgKind.Null:
                return JsHandle.Null;
            case ArgKind.String:
            {
                var handle = interop.StringNew(_string!);
                return handle;
            }
            case ArgKind.Number:
            {
                var handle = interop.NumberNew(_number);
                return handle;
            }
            case ArgKind.Boolean:
                return interop.BooleanNew(_boolean);
            case ArgKind.Bytes:
            {
                var handle = interop.BytesNew(_bytes);
                return handle;
            }
            case ArgKind.Object:
                return interop.Clone(_object!.Handle);
            default:
                return JsHandle.Undefined;
        }
    }
}
