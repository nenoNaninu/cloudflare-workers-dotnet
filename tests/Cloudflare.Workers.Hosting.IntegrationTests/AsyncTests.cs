namespace Cloudflare.Workers.Hosting.IntegrationTests;

/// <summary>Timers, concurrency, ExecutionContext.waitUntil and scheduled (cron) events.</summary>
public class AsyncTests(WorkerFixture worker) : E2ETestBase(worker)
{
    [Fact]
    public async Task Delay_CompletesAfterTheRequestedTime()
    {
        var start = DateTime.UtcNow;
        Assert.Equal("slept 400", await Client.GetStringAsync("delay/400", Ct));
        Assert.True(DateTime.UtcNow - start >= TimeSpan.FromMilliseconds(350));
    }

    [Fact]
    public async Task Delays_InsideOneRequestOverlap()
    {
        int elapsed = int.Parse(await Client.GetStringAsync("parallel-delay", Ct));
        Assert.InRange(elapsed, 250, 1200); // five 300 ms timers, not 1500 ms
    }

    [Fact]
    public async Task ConcurrentRequests_AreServedIndependently()
    {
        var tasks = Enumerable.Range(0, 20)
            .Select(i => Client.GetStringAsync($"delay/{50 + i * 10}", Ct));
        var results = await Task.WhenAll(tasks);

        Assert.Equal(Enumerable.Range(0, 20).Select(i => $"slept {50 + i * 10}"), results);
    }

    [Fact]
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

    [Fact]
    public async Task WaitUntil_RunsAfterTheResponseIsSent()
    {
        string key = UniqueKey("wait-until");
        using var gate = Worker.CreateWaitUntilGate(key);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));

        var responseTask = Client.GetAsync($"wait-until/{key}", timeout.Token);
        await gate.Started.WaitAsync(timeout.Token);
        // The upstream request is still blocked, so awaiting waitUntil before responding must fail.
        using var response = await responseTask;
        Assert.Equal(System.Net.HttpStatusCode.Accepted, response.StatusCode);
        using var missing = await Client.GetAsync($"kv/{key}", timeout.Token);
        Assert.Equal(System.Net.HttpStatusCode.NotFound, missing.StatusCode);

        gate.Release();
        string value = await PollAsync(() => TryGetKvAsync(key));
        Assert.Equal("done-after-response", value);
    }

    [Fact]
    public async Task ScheduledEvent_InvokesTheCronHandler()
    {
        // `wrangler dev --test-scheduled` exposes the scheduled handler over HTTP.
        using var trigger = await Client.GetAsync("__scheduled?cron=*%2F5+*+*+*+*", Ct);
        Assert.True(trigger.IsSuccessStatusCode, await trigger.Content.ReadAsStringAsync(Ct));

        string value = await PollAsync(() => TryGetKvAsync("scheduled:last"));
        Assert.Equal("*/5 * * * *|True", value);
    }

    [Fact]
    public async Task ManyHandleAllocations_InOneRequestDoNotFail()
    {
        Assert.Equal("300", await Client.GetStringAsync("stress/handles/300", Ct));
        Assert.Equal("pong", await Client.GetStringAsync("ping", Ct));
    }
}
