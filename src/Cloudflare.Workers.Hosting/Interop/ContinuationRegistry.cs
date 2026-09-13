namespace Cloudflare.Workers.Hosting.Interop;

/// <summary>
/// Registers continuations to run after JavaScript promises settle or other asynchronous operations complete.
/// </summary>
public static class ContinuationRegistry
{
    private static readonly Dictionary<int, Action<bool, JsHandle>> Continuations = [];
    private static int ContinuationId;
    private static readonly Lock Lock = new();

    public static int Register(Action<bool, JsHandle> continuation)
    {
        lock (Lock)
        {
            int id = ++ContinuationId;
            Continuations.Add(id, continuation);
            return id;
        }
    }

    public static void Complete(int continuationId, bool isSuccess, JsHandle handle)
    {
        Action<bool, JsHandle>? continuation;

        lock (Lock)
        {
            if (!Continuations.Remove(continuationId, out continuation))
            {
                return;
            }
        }

        continuation(isSuccess, handle);
    }
}
