namespace Cloudflare.Workers.Hosting.Interop;

public enum JsValueKind
{
    Undefined = 0,
    Null = 1,
    Boolean = 2,
    Number = 3,
    String = 4,
    BigInt = 5,
    Symbol = 6,
    Function = 7,
    Object = 8,
}
