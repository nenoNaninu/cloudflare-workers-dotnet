using Cloudflare.Workers.Hosting.Tests.Fakes;
using Xunit;

namespace Cloudflare.Workers.Hosting.Tests;

public class ContextAndTimeTests
{
    // Under xUnit's SynchronizationContext, await continuations may hop to the
    // thread pool instead of running inline; wait for the promise to settle.
    private static void WaitForSettlement(FakePromise promise)
    {
        SpinWait.SpinUntil(() => promise.State != FakePromise.PromiseState.Pending, TimeSpan.FromSeconds(5));
        Assert.NotEqual(FakePromise.PromiseState.Pending, promise.State);
    }

    [Fact]
    public void WaitUntil_PassesAPromiseThatSettlesWithTheTask()
    {
        var (interop, runtime) = FakeWorld.Create();
        var received = new List<object?>();
        var ctxObject = new FakeObject
        {
            ["waitUntil"] = new FakeFunction((_, args) =>
            {
                received.Add(args[0]);
                return JsUndefined.Value;
            }),
        };
        using var context = FakeWorld.CreateContext(runtime, interop, ctxObject);

        var completion = new TaskCompletionSource();
        context.WaitUntil(completion.Task);

        var promise = Assert.IsType<FakePromise>(Assert.Single(received));
        Assert.Equal(FakePromise.PromiseState.Pending, promise.State);

        completion.SetResult();
        WaitForSettlement(promise);
        Assert.Equal(FakePromise.PromiseState.Fulfilled, promise.State);
    }

    [Fact]
    public void WaitUntil_FailedTaskRejectsThePromise()
    {
        var (interop, runtime) = FakeWorld.Create();
        var received = new List<object?>();
        var ctxObject = new FakeObject
        {
            ["waitUntil"] = new FakeFunction((_, args) =>
            {
                received.Add(args[0]);
                return JsUndefined.Value;
            }),
        };
        using var context = FakeWorld.CreateContext(runtime, interop, ctxObject);

        var completion = new TaskCompletionSource();
        context.WaitUntil(completion.Task);
        completion.SetException(new InvalidOperationException("background failed"));

        var promise = Assert.IsType<FakePromise>(Assert.Single(received));
        WaitForSettlement(promise);
        Assert.Equal(FakePromise.PromiseState.Rejected, promise.State);
    }

    [Fact]
    public void PassThroughOnException_CallsIntoJs()
    {
        var (interop, runtime) = FakeWorld.Create();
        bool called = false;
        var ctxObject = new FakeObject
        {
            ["passThroughOnException"] = new FakeFunction((_, _) =>
            {
                called = true;
                return JsUndefined.Value;
            }),
        };
        using var context = FakeWorld.CreateContext(runtime, interop, ctxObject);

        context.PassThroughOnException();

        Assert.True(called);
    }

    [Fact]
    public async Task Delay_CompletesWhenTheTimerFires()
    {
        var (interop, runtime) = FakeWorld.Create();
        interop.AutoFireTimeouts = false;

        var task = WorkerTimer.Delay(runtime, 25);
        Assert.False(task.IsCompleted);

        interop.FireTimeouts();
        await task;
    }
}
