namespace Cloudflare.Workers.Hosting.Interop;

/// <summary>Registers JavaScript callbacks that remain active until explicitly unregistered.</summary>
public static class CallbackRegistry
{
    private static readonly Dictionary<int, Action<JsHandle>> Callbacks = [];
    private static readonly Lock Lock = new();
    private static int CallbackId;

    public static int Register(Action<JsHandle> callback)
    {
        lock (Lock)
        {
            int id = ++CallbackId;
            Callbacks.Add(id, callback);
            return id;
        }
    }

    /// <summary>
    /// Invokes a callback without removing it. The registry does not manage handle ownership.
    /// Calls to an unregistered ID are ignored.
    /// </summary>
    public static void Invoke(int callbackId, JsHandle handle)
    {
        Action<JsHandle>? callback;

        lock (Lock)
        {
            if (!Callbacks.TryGetValue(callbackId, out callback))
            {
                return;
            }
        }

        callback(handle);
    }

    public static void Unregister(int callbackId)
    {
        lock (Lock)
        {
            Callbacks.Remove(callbackId);
        }
    }
}
