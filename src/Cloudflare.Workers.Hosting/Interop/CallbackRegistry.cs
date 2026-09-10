namespace Cloudflare.Workers.Hosting.Interop;

public static class CallbackRegistry
{
    private static readonly Dictionary<int, Action<bool, JsHandle>> Callbacks = [];
    private static int NextId;
    private static readonly Lock Lock = new();

    public static int Register(Action<bool, JsHandle> callback)
    {
        lock (Lock)
        {
            int id = ++NextId;
            Callbacks.Add(id, callback);
            return id;
        }
    }

    public static void Complete(int callbackId, bool success, JsHandle handle)
    {
        Action<bool, JsHandle>? callback;

        lock (Lock)
        {
            if (!Callbacks.Remove(callbackId, out callback))
            {
                return;
            }
        }

        callback!(success, handle);
    }
}
