using Cloudflare.Workers.Hosting.Interop;
using Cloudflare.Workers.Hosting.Tests.Fakes;
using Xunit;

namespace Cloudflare.Workers.Hosting.Tests;

public class JsInteropTests
{
    [Fact]
    public void JsHandle_UsesValueEquality()
    {
        var left = new JsHandle(42);
        var right = new JsHandle(42);

        Assert.Equal(left, right);
        Assert.True(left == right);
        Assert.Equal("JsHandle(42)", left.ToString());
    }

    [Fact]
    public void StringRoundtrip()
    {
        var (interop, runtime) = FakeWorld.Create();
        using var value = runtime.WrapToJsObject(interop.StringNew("Hello World"));

        Assert.Equal(JsValueKind.String, value.Kind);
        Assert.Equal("Hello World", value.AsString());
    }

    [Fact]
    public void PropertyGetAndSet()
    {
        var (interop, runtime) = FakeWorld.Create();
        using var obj = runtime.NewObject();
        obj.SetProperty("name", JsArg.From("worker"));
        obj.SetProperty("count", JsArg.From(42));
        obj.SetProperty("enabled", JsArg.From(true));

        Assert.Equal("worker", obj.GetPropertyAsString("name"));
        Assert.Equal(42d, obj.GetPropertyAsNumber("count"));
        Assert.Equal(true, obj.GetPropertyAsBoolean("enabled"));
        Assert.Null(obj.GetPropertyAsString("missing"));
    }

    [Fact]
    public void Call_ThrowingMethodSurfacesAsJsException()
    {
        var (interop, runtime) = FakeWorld.Create();
        var target = new FakeObject
        {
            ["boom"] = new FakeFunction((_, _) => throw FakeJsThrow.WithMessage("kaboom")),
        };
        using var obj = runtime.WrapToJsObject(interop.Retain(target));

        var exception = Assert.Throws<JsException>(() => obj.Call("boom"));
        Assert.Contains("kaboom", exception.Message);
    }

    [Fact]
    public void Call_MissingMethodSurfacesAsJsException()
    {
        var (interop, runtime) = FakeWorld.Create();
        using var obj = runtime.NewObject();

        var exception = Assert.Throws<JsException>(() => obj.Call("nope"));
        Assert.Contains("not a function", exception.Message);
    }

    [Fact]
    public void Call_ReleasesCreatedHandleWithoutReleasingSourceObject()
    {
        var (interop, runtime) = FakeWorld.Create();
        int handlesObservedDuringCall = -1;
        var target = new FakeObject
        {
            ["read"] = new FakeFunction((_, args) =>
            {
                handlesObservedDuringCall = interop.LiveHandleCount;
                return ((FakeObject)args[0]!)["value"];
            }),
        };
        using var obj = runtime.WrapToJsObject(interop.Retain(target));
        using var argument = runtime.NewObject();
        argument.SetProperty("value", JsArg.From("kept"));
        int handlesBeforeCall = interop.LiveHandleCount;

        using var result = obj.Call("read", JsArg.From(argument));

        Assert.Equal("kept", result.AsString());
        Assert.Equal(handlesBeforeCall + 1, handlesObservedDuringCall);
        Assert.Equal("kept", argument.GetPropertyAsString("value"));
    }

    [Fact]
    public async Task AwaitPromise_ResolvedPromiseYieldsValue()
    {
        var (interop, runtime) = FakeWorld.Create();
        using var promise = runtime.WrapToJsObject(interop.Retain(FakePromise.Resolved("done")));

        using var result = await runtime.AwaitPromise(promise);

        Assert.Equal("done", result.AsString());
    }

    [Fact]
    public async Task AwaitPromise_RejectedPromiseThrowsJsException()
    {
        var (interop, runtime) = FakeWorld.Create();
        using var promise = runtime.WrapToJsObject(interop.Retain(
            FakePromise.Rejected(new FakeObject { ["message"] = "denied" })));

        var exception = await Assert.ThrowsAsync<JsException>(async () => await runtime.AwaitPromise(promise));
        Assert.Contains("denied", exception.Message);
    }

    [Fact]
    public async Task AwaitPromise_PendingPromiseCompletesWhenSettled()
    {
        var (interop, runtime) = FakeWorld.Create();
        var pending = new FakePromise();
        using var promise = runtime.WrapToJsObject(interop.Retain(pending));

        var task = runtime.AwaitPromise(promise);
        Assert.False(task.IsCompleted);

        pending.Resolve(123d);

        using var result = await task;
        Assert.Equal(123d, result.AsNumber());
    }

    [Fact]
    public async Task AwaitPromise_NonPromiseValueResolvesImmediately()
    {
        var (interop, runtime) = FakeWorld.Create();
        using var value = runtime.WrapToJsObject(interop.StringNew("plain"));

        using var result = await runtime.AwaitPromise(value);

        Assert.Equal("plain", result.AsString());
    }

    [Fact]
    public void JsonStringify_SerializesObjectGraph()
    {
        var (interop, runtime) = FakeWorld.Create();
        var value = new FakeObject
        {
            ["name"] = "worker",
            ["tags"] = new FakeArray(["a", "b"]),
            ["nested"] = new FakeObject { ["n"] = 1d },
        };
        using var obj = runtime.WrapToJsObject(interop.Retain(value));

        string json = runtime.JsonStringify(obj);

        Assert.Equal("""{"name":"worker","tags":["a","b"],"nested":{"n":1}}""", json);
    }

    [Fact]
    public void JsonParse_ProducesReadableObject()
    {
        var (_, runtime) = FakeWorld.Create();

        using var parsed = runtime.JsonParse("""{"id":7,"label":"x"}""");

        Assert.Equal(7d, parsed.GetPropertyAsNumber("id"));
        Assert.Equal("x", parsed.GetPropertyAsString("label"));
    }

    [Fact]
    public void BytesRoundtrip()
    {
        var (interop, runtime) = FakeWorld.Create();
        byte[] payload = [1, 2, 3, 255];
        using var bytes = runtime.WrapToJsObject(interop.BytesNew(payload));

        Assert.Equal(payload, bytes.AsBytes());
    }

    [Fact]
    public void Release_ReusesSlots()
    {
        var (interop, _) = FakeWorld.Create();
        var first = interop.StringNew("a");
        interop.ReleaseHandle(first);
        var second = interop.StringNew("b");

        Assert.Equal(first.Value, second.Value);
        Assert.Equal("b", interop.StringGet(second));
    }
}
