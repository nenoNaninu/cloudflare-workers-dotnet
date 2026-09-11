using Cloudflare.Workers.Hosting.Tests.Fakes;
using Xunit;

namespace Cloudflare.Workers.Hosting.Tests;

public class FetchTests
{
    [Fact]
    public async Task FetchRequestMessage_CollectionInitializerSetsHeadersAndPassesValuesToFetch()
    {
        var (interop, runtime) = FakeWorld.Create();
        var captured = new List<object?[]>();
        interop.Globals["fetch"] = new FakeFunction((_, args) =>
        {
            captured.Add(args);
            return FakePromise.Resolved(new FakeObject { ["status"] = 204d });
        });

        var request = new FetchRequestMessage("https://api.example.com/messages")
        {
            Method = "POST",
            Body = """{"message":"Hello from C#!"}""",
            Headers =
            {
                { "Content-Type", "application/json" },
                { "Accept", "application/json" }
            }
        };

        Assert.Equal("https://api.example.com/messages", request.Url);
        Assert.Equal("POST", request.Method);
        Assert.Equal("""{"message":"Hello from C#!"}""", request.Body);
        Assert.Equal(2, request.Headers.Count);
        Assert.Equal("application/json", request.Headers.Get("Content-Type"));
        Assert.Equal("application/json", request.Headers.Get("Accept"));

        using var response = await Fetch.FetchAsync(runtime, request);

        var args = Assert.Single(captured);
        Assert.Equal("https://api.example.com/messages", args[0]);
        var init = Assert.IsType<FakeObject>(args[1]);
        Assert.Equal("POST", init["method"]);
        Assert.Equal("""{"message":"Hello from C#!"}""", init["body"]);
        var headers = Assert.IsType<FakeArray>(init["headers"]);
        Assert.Collection(headers,
            pair => Assert.Equal(["Content-Type", "application/json"], Assert.IsType<FakeArray>(pair).Cast<string>()),
            pair => Assert.Equal(["Accept", "application/json"], Assert.IsType<FakeArray>(pair).Cast<string>()));
    }

    [Fact]
    public async Task FetchAsync_ReturnsWrappedResponse()
    {
        var (interop, runtime) = FakeWorld.Create();
        interop.Globals["fetch"] = new FakeFunction((_, _) => FakePromise.Resolved(new FakeObject
        {
            ["status"] = 200d,
            ["text"] = new FakeFunction((_, _) => FakePromise.Resolved("remote content")),
        }));

        using var response = await Fetch.FetchAsync(runtime, new FetchRequestMessage("https://example.com/"));

        Assert.Equal(200, response.StatusCode);
        Assert.Equal("remote content", await response.ReadAsStringAsync());
    }

    [Fact]
    public async Task FetchAsync_PassesUrlAndInit()
    {
        var (interop, runtime) = FakeWorld.Create();
        var captured = new List<object?[]>();
        interop.Globals["fetch"] = new FakeFunction((_, args) =>
        {
            captured.Add(args);
            return FakePromise.Resolved(new FakeObject { ["status"] = 204d });
        });

        var request = new FetchRequestMessage("https://api.example.com/items")
        {
            Method = "POST",
            Body = "payload",
            Redirect = FetchRedirectMode.Manual,
        };
        request.Headers.Replace("x-api-key", "secret");

        using var response = await Fetch.FetchAsync(runtime, request);

        var args = Assert.Single(captured);
        Assert.Equal("https://api.example.com/items", args[0]);
        var init = Assert.IsType<FakeObject>(args[1]);
        Assert.Equal("POST", init["method"]);
        Assert.Equal("payload", init["body"]);
        Assert.Equal("manual", init["redirect"]);
        var headerPair = Assert.IsType<FakeArray>(Assert.IsType<FakeArray>(init["headers"])[0]);
        Assert.Equal(["x-api-key", "secret"], headerPair.Cast<string>());
    }

    [Theory]
    [InlineData(FetchRedirectMode.Follow, null)]
    [InlineData(FetchRedirectMode.Manual, "manual")]
    [InlineData(FetchRedirectMode.Error, "error")]
    public void FetchRequestMessage_MapsRedirectMode(FetchRedirectMode mode, string? expected)
    {
        var (interop, runtime) = FakeWorld.Create();
        var request = new FetchRequestMessage("https://example.com/") { Redirect = mode };

        using var js = request.ToJsObject(runtime);

        var init = Assert.IsType<FakeObject>(interop.Get(js.Handle));
        if (expected is null)
        {
            Assert.False(init.ContainsKey("redirect"));
        }
        else
        {
            Assert.Equal(expected, init["redirect"]);
        }
    }

    [Fact]
    public void FetchRequestMessage_RejectsUnsupportedRedirectMode()
    {
        var (interop, runtime) = FakeWorld.Create();
        var request = new FetchRequestMessage("https://example.com/")
        {
            Redirect = (FetchRedirectMode)999,
        };

        var exception = Assert.Throws<InvalidOperationException>(() => request.ToJsObject(runtime));

        Assert.Contains("The redirect mode must be Follow, Manual, or Error.", exception.Message);
        Assert.Equal(0, interop.LiveHandleCount);
    }

    [Fact]
    public async Task FetchAsync_RejectedPromiseThrows()
    {
        var (interop, runtime) = FakeWorld.Create();
        interop.Globals["fetch"] = new FakeFunction((_, _) =>
            FakePromise.Rejected(new FakeObject { ["message"] = "network down" }));

        var exception = await Assert.ThrowsAsync<Cloudflare.Workers.Hosting.Interop.JsException>(
            async () => await Fetch.FetchAsync(runtime, new FetchRequestMessage("https://example.com/")));
        Assert.Contains("network down", exception.Message);
    }
}
