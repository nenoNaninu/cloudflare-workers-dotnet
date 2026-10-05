namespace Cloudflare.Workers.Hosting.IntegrationTests;

/// <summary>Timers, concurrency, ExecutionContext.waitUntil and scheduled (cron) events.</summary>
public class AsyncTests(WorkerFixture worker) : E2ETestBase(worker)
{
    [E2EFact]
    public async Task Delay_CompletesAfterTheRequestedTime()
    {
        var start = DateTime.UtcNow;
        Assert.Equal("slept 400", await Client.GetStringAsync("delay/400", Ct));
        Assert.True(DateTime.UtcNow - start >= TimeSpan.FromMilliseconds(350));
    }

    [E2EFact]
    public async Task Delays_InsideOneRequestOverlap()
    {
        int elapsed = int.Parse(await Client.GetStringAsync("parallel-delay", Ct));
        Assert.InRange(elapsed, 250, 1200); // five 300 ms timers, not 1500 ms
    }

    [E2EFact]
    public async Task ConcurrentRequests_AreServedIndependently()
    {
        var tasks = Enumerable.Range(0, 20)
            .Select(i => Client.GetStringAsync($"delay/{50 + i * 10}", Ct));
        var results = await Task.WhenAll(tasks);

        Assert.Equal(Enumerable.Range(0, 20).Select(i => $"slept {50 + i * 10}"), results);
    }

    [E2EFact]
    public async Task ConcurrentRequests_KeepTheirOwnBodies()
    {
        var tasks = Enumerable.Range(0, 20).Select(async i =>
        {
            string payload = $"payload-{i}-" + new string('x', i * 100);
            using var response = await Client.PostAsync("echo/text", Text(payload), Ct);
            return (payload, echoed: await response.Content.ReadAsStringAsync(Ct));
        });

        foreach (var (payload, echoed) in await Task.WhenAll(tasks))
        {
            Assert.Equal(payload, echoed);
        }
    }

    [E2EFact]
    public async Task WaitUntil_RunsAfterTheResponseIsSent()
    {
        string key = UniqueKey("wait-until");
        using var response = await Client.GetAsync($"wait-until/{key}", Ct);
        Assert.Equal(System.Net.HttpStatusCode.Accepted, response.StatusCode);

        string value = await PollAsync(() => TryGetKvAsync(key));
        Assert.Equal("done-after-response", value);
    }

    [E2EFact]
    public async Task ScheduledEvent_InvokesTheCronHandler()
    {
        // `wrangler dev --test-scheduled` exposes the scheduled handler over HTTP.
        using var trigger = await Client.GetAsync("cdn-cgi/handler/scheduled?cron=*%2F5+*+*+*+*", Ct);
        Assert.True(trigger.IsSuccessStatusCode, await trigger.Content.ReadAsStringAsync(Ct));

        string value = await PollAsync(() => TryGetKvAsync("scheduled:last"));
        Assert.Equal("*/5 * * * *|True", value);
    }

    [E2EFact]
    public async Task ManyHandleAllocations_InOneRequestDoNotFail()
    {
        Assert.Equal("300", await Client.GetStringAsync("stress/handles/300", Ct));
        Assert.Equal("pong", await Client.GetStringAsync("ping", Ct));
    }
}
