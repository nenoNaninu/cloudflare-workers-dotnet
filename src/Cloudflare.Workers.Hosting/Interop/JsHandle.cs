namespace Cloudflare.Workers.Hosting.Interop;

public readonly record struct JsHandle(int Value)
{
    public static readonly JsHandle Undefined = new(0);
    public static readonly JsHandle Null = new(1);
    public static readonly JsHandle True = new(2);
    public static readonly JsHandle False = new(3);

    public bool IsReserved => Value is >= 0 and < 4;

    public override string ToString() => $"JsHandle({Value})";
}
