using Cloudflare.Workers.Hosting.Interop;

namespace Cloudflare.Workers.Hosting;

public sealed class ScheduledEvent : IDisposable
{
    private readonly JsObject _js;

    private ScheduledEvent(JsObject js)
    {
        _js = js;
        Cron = js.GetPropertyAsString("cron") ?? string.Empty;
        double? time = js.GetPropertyAsNumber("scheduledTime");
        ScheduledTime = time is null
            ? DateTimeOffset.UnixEpoch
            : DateTimeOffset.FromUnixTimeMilliseconds((long)time.Value);
    }

    internal static ScheduledEvent FromJsObject(JsObject js)
    {
        try
        {
            return new ScheduledEvent(js);
        }
        catch
        {
            js.Dispose();
            throw;
        }
    }

    /// <summary>The cron expression that fired.</summary>
    public string Cron { get; }

    public DateTimeOffset ScheduledTime { get; }

    public JsObject Js => _js;

    public void Dispose() => _js.Dispose();
}
