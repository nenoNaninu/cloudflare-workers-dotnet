namespace Cloudflare.Workers.Hosting.Interop;

public sealed class JsException : Exception
{
    public JsException(string message)
        : base(message)
    {
    }
}
