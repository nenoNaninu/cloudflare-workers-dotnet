using System.Text.Json;
using Cloudflare.Workers.Hosting.Interop;
using Cloudflare.Workers.Hosting.Tests.Fakes;
using Xunit;

namespace Cloudflare.Workers.Hosting.Tests;

public class BrowserBindingTests
{
    [Fact]
    public void Browser_ThrowsForMissingBinding()
    {
        var (interop, runtime) = FakeWorld.Create();
        using (var env = FakeWorld.CreateEnv(runtime, interop))
        {
            Assert.Contains("BROWSER", Assert.Throws<InvalidOperationException>(() => env.Browser("BROWSER")).Message);
        }
        Assert.Equal(0, interop.LiveHandleCount);
    }

    [Fact]
    public async Task Acquire_PreservesOptionsAndNativeOutboundFetchers()
    {
        var (interop, runtime) = FakeWorld.Create();
        var outbound = new FakeObject();
        var target = new FakeObject { ["id"] = "page-1", ["type"] = "page", ["url"] = "about:blank" };
        var binding = new FakeObject();
        binding["acquire"] = new FakeFunction((receiver, args) =>
        {
            Assert.Same(binding, receiver);
            var options = Assert.IsType<FakeObject>(Assert.Single(args));
            Assert.Equal(10000d, options["keepAlive"]);
            Assert.Equal(false, options["recording"]);
            Assert.Equal(true, options["targets"]);
            Assert.Equal("JP", options["location"]);
            Assert.Equal(300000d, options["liveViewUrlExpiresInMs"]);
            Assert.False(options.ContainsKey("keep_alive"));
            var guardrails = Assert.IsType<FakeObject>(options["guardrails"]);
            Assert.Empty(Assert.IsType<FakeArray>(guardrails["allowedDomains"]));
            Assert.Equal("cdn", Assert.Single(Assert.IsType<FakeArray>(guardrails["allowedDomainSets"])));
            Assert.Same(outbound, Assert.IsType<FakeObject>(options["outboundByHost"])["example.com"]);
            return FakePromise.Resolved(new FakeObject { ["sessionId"] = "session-1", ["targets"] = new FakeArray([target]) });
        });

        using (var env = FakeWorld.CreateEnv(runtime, interop, new FakeObject { ["BROWSER"] = binding, ["OUTBOUND"] = outbound }))
        using (var browser = env.Browser("BROWSER"))
        using (var worker = env.Service("OUTBOUND"))
        {
            var result = await browser.AcquireAsync(new BrowserAcquireOptions
            {
                KeepAlive = 10000,
                Recording = false,
                Location = "JP",
                Targets = true,
                LiveViewUrlExpiresInMs = 300000,
                Guardrails = new() { AllowedDomains = [], AllowedDomainSets = ["cdn"] },
                OutboundByHost = new Dictionary<string, ServiceBinding> { ["example.com"] = worker },
            });
            Assert.Equal("session-1", result.SessionId);
            Assert.Equal("page-1", Assert.Single(result.Targets!).Id);
        }
        Assert.Equal(0, interop.LiveHandleCount);
    }

    [Fact]
    public async Task LaunchAndConnect_PreserveSessionPinnedFetcherAndReleaseHandles()
    {
        var (interop, runtime) = FakeWorld.Create();
        var fetcher = new FakeObject
        {
            ["fetch"] = new FakeFunction((_, args) =>
            {
                Assert.Equal("https://browser-binding.invalid", args[0]);
                var headers = Assert.IsType<FakeArray>(Assert.IsType<FakeObject>(args[1])["headers"]);
                Assert.Equal(new object?[] { "Upgrade", "websocket" }, Assert.IsType<FakeArray>(Assert.Single(headers)));
                return FakePromise.Resolved(new FakeObject { ["status"] = 101d });
            }),
        };
        // A Fetcher is a capability, not JSON data: never stringify it.
        var connection = new FakeObject { ["sessionId"] = "s", ["webSocket"] = fetcher };
        var json = Assert.IsType<FakeObject>(interop.Globals["JSON"]);
        var stringify = Assert.IsType<FakeFunction>(json["stringify"]);
        json["stringify"] = new FakeFunction((receiver, args) =>
        {
            Assert.NotSame(connection, args[0]);
            Assert.NotSame(fetcher, args[0]);
            return stringify.Invoke(receiver, args);
        });
        var binding = new FakeObject
        {
            ["launch"] = new FakeFunction((_, args) =>
            {
                Assert.IsType<JsUndefined>(Assert.Single(args));
                return FakePromise.Resolved(connection);
            }),
            ["connectSession"] = new FakeFunction((_, args) =>
            {
                Assert.Equal("s", args[0]);
                Assert.Equal("page", Assert.IsType<FakeObject>(args[1])["targetId"]);
                return FakePromise.Resolved(connection);
            }),
        };
        using (var env = FakeWorld.CreateEnv(runtime, interop, new FakeObject { ["BROWSER"] = binding }))
        using (var browser = env.Browser("BROWSER"))
        {
            using var launched = await browser.LaunchAsync();
            using var connected = await browser.ConnectSessionAsync("s", new() { TargetId = "page" });
            Assert.Equal("s", launched.SessionId);
            Assert.Null(launched.Targets);
            Assert.Same(fetcher, interop.Get(connected.WebSocket.Js.Handle));
            using var response = await connected.WebSocket.FetchAsync(new FetchRequestMessage("https://browser-binding.invalid")
            {
                Headers = { { "Upgrade", "websocket" } },
            });
            Assert.Equal(101, response.StatusCode);
        }
        Assert.Equal(0, interop.LiveHandleCount);
    }

    [Fact]
    public async Task SessionMethods_MapResultsPaginationAndNull()
    {
        var (interop, runtime) = FakeWorld.Create();
        var session = Parse(runtime, interop, """{"sessionId":"s","startTime":123,"endTime":456,"closeReason":1,"closeReasonText":"NormalClosure","connectionId":"c","connectionStartTime":124,"connectionEndTime":455,"lastUpdated":457,"webSocketDebuggerUrl":"wss://cdp","devtoolsFrontendUrl":"https://view"}""");
        var sessions = new FakeArray([session]);
        var binding = new FakeObject
        {
            ["acquire"] = new FakeFunction((_, args) =>
            {
                Assert.IsType<JsUndefined>(Assert.Single(args));
                return FakePromise.Resolved(new FakeObject { ["sessionId"] = "s" });
            }),
            ["listSessions"] = new FakeFunction((_, args) =>
            {
                var options = Assert.IsType<FakeObject>(Assert.Single(args));
                Assert.Equal(5d, options["limit"]);
                Assert.Equal(0d, options["offset"]);
                return FakePromise.Resolved(sessions);
            }),
            ["history"] = new FakeFunction((_, args) =>
            {
                Assert.IsType<JsUndefined>(Assert.Single(args));
                return FakePromise.Resolved(sessions);
            }),
            ["getSession"] = new FakeFunction((_, args) => FakePromise.Resolved((string)args[0]! == "s" ? session : null)),
            ["limits"] = new FakeFunction((_, args) =>
            {
                Assert.Empty(args);
                return FakePromise.Resolved(Parse(runtime, interop, """{"activeSessions":[{"id":"s"}],"maxConcurrentSessions":10,"allowedBrowserAcquisitions":2,"timeUntilNextAllowedBrowserAcquisition":500,"usedBrowserTimeSeconds":0}"""));
            }),
            ["closeSession"] = new FakeFunction((_, args) =>
            {
                Assert.Equal("s", Assert.Single(args));
                return FakePromise.Resolved(new FakeObject { ["status"] = "closing" });
            }),
        };
        using (var env = FakeWorld.CreateEnv(runtime, interop, new FakeObject { ["BROWSER"] = binding }))
        using (var browser = env.Browser("BROWSER"))
        {
            Assert.Null((await browser.AcquireAsync()).Targets);
            var result = Assert.Single(await browser.ListSessionsAsync(new() { Limit = 5, Offset = 0 }));
            Assert.Equal(123d, result.StartTime);
            Assert.Equal(1, result.CloseReason);
            Assert.Equal("c", result.ConnectionId);
            Assert.Equal(457d, result.LastUpdated);
            Assert.Equal(result, Assert.Single(await browser.HistoryAsync()));
            Assert.Equal(result, await browser.GetSessionAsync("s"));
            Assert.Null(await browser.GetSessionAsync("missing"));
            var limits = await browser.LimitsAsync();
            Assert.Equal("s", Assert.Single(limits.ActiveSessions).Id);
            Assert.Equal(10d, limits.MaxConcurrentSessions);
            Assert.Equal(2d, limits.AllowedBrowserAcquisitions);
            Assert.Equal(500d, limits.TimeUntilNextAllowedBrowserAcquisition);
            Assert.Equal(0d, limits.UsedBrowserTimeSeconds);
            Assert.Equal("closing", (await browser.CloseSessionAsync("s")).Status);
        }
        Assert.Equal(0, interop.LiveHandleCount);
    }

    [Fact]
    public async Task LiveView_MapsNestedOptionsAndResult()
    {
        var (interop, runtime) = FakeWorld.Create();
        var binding = new FakeObject
        {
            ["getLiveView"] = new FakeFunction((_, args) =>
            {
                Assert.Equal("s", args[0]);
                var options = Assert.IsType<FakeObject>(args[1]);
                Assert.Equal("tab", options["mode"]);
                Assert.Equal("p", options["targetId"]);
                Assert.Equal(10000d, options["expiresInMs"]);
                Assert.Equal("readonly", Assert.IsType<FakeObject>(options["guardrails"])["mode"]);
                return FakePromise.Resolved(new FakeObject
                {
                    ["id"] = "view", ["webSocketDebuggerUrl"] = "wss://cdp", ["devtoolsFrontendUrl"] = "https://view", ["options"] = options,
                });
            }),
        };
        using (var env = FakeWorld.CreateEnv(runtime, interop, new FakeObject { ["BROWSER"] = binding }))
        using (var browser = env.Browser("BROWSER"))
        {
            var view = await browser.GetLiveViewAsync("s", new() { Mode = "tab", TargetId = "p", ExpiresInMs = 10000, Guardrails = new() });
            Assert.Equal("view", view.Id);
            Assert.Equal("https://view", view.DevtoolsFrontendUrl);
            Assert.Equal("wss://cdp", view.WebSocketDebuggerUrl);
            Assert.Equal("readonly", view.Options.Guardrails!.Mode);
        }
        Assert.Equal(0, interop.LiveHandleCount);
    }

    [Fact]
    public async Task DevTools_UsesNestedReceiverAndCorrectArguments()
    {
        var (interop, runtime) = FakeWorld.Create();
        var target = Parse(runtime, interop, """{"id":"p","type":"page","url":"about:blank","title":"Page","description":"test","webSocketDebuggerUrl":"wss://cdp","devtoolsFrontendUrl":"https://view"}""");
        var calls = new List<(string Method, object?[] Args)>();
        var nested = new FakeObject();
        foreach (string method in new[] { "getVersion", "getProtocol", "listTargets", "getTarget", "newTarget", "activateTarget", "closeTarget" })
        {
            nested[method] = new FakeFunction((receiver, args) =>
            {
                Assert.Same(nested, receiver);
                calls.Add((method, args));
                Assert.Equal("s", args[0]);
                return FakePromise.Resolved(method switch
                {
                    "getVersion" => Parse(runtime, interop, """{"Browser":"Chrome/1","Protocol-Version":"1.3","User-Agent":"UA","V8-Version":"v8","WebKit-Version":"wk","webSocketDebuggerUrl":"wss://cdp"}"""),
                    "getProtocol" => Parse(runtime, interop, """{"domains":[{"domain":"Page","commands":[{"name":"navigate","extra":true}]}],"extra":42}"""),
                    "listTargets" => new FakeArray([target]),
                    "activateTarget" or "closeTarget" => new FakeObject { ["message"] = "ok" },
                    _ => target,
                });
            });
        }
        using (var env = FakeWorld.CreateEnv(runtime, interop, new FakeObject { ["BROWSER"] = new FakeObject { ["devtools"] = nested } }))
        using (var browser = env.Browser("BROWSER"))
        using (var devtools = browser.DevTools)
        {
            var version = await devtools.GetVersionAsync("s");
            Assert.Equal(new BrowserVersion("Chrome/1", "1.3", "UA", "v8", "wk", "wss://cdp"), version);
            JsonElement protocol = await devtools.GetProtocolAsync("s");
            Assert.Equal(42, protocol.GetProperty("extra").GetInt32());
            Assert.Equal("navigate", protocol.GetProperty("domains")[0].GetProperty("commands")[0].GetProperty("name").GetString());
            Assert.Equal("p", Assert.Single(await devtools.ListTargetsAsync("s", new() { LiveViewUrlExpiresInMs = 300000 })).Id);
            Assert.Equal(300000d, Assert.IsType<FakeObject>(calls[^1].Args[1])["liveViewUrlExpiresInMs"]);
            Assert.Equal("Page", (await devtools.GetTargetAsync("s", "p")).Title);
            Assert.Equal("p", calls[^1].Args[1]);
            Assert.Equal("p", (await devtools.NewTargetAsync("s", options: new() { LiveViewUrlExpiresInMs = 10000 })).Id);
            Assert.IsType<JsUndefined>(calls[^1].Args[1]);
            Assert.Equal(10000d, Assert.IsType<FakeObject>(calls[^1].Args[2])["liveViewUrlExpiresInMs"]);
            await devtools.NewTargetAsync("s", "https://example.com");
            Assert.Equal("https://example.com", calls[^1].Args[1]);
            Assert.IsType<JsUndefined>(calls[^1].Args[2]);
            Assert.Equal("ok", (await devtools.ActivateTargetAsync("s", "p")).Message);
            Assert.Equal("p", calls[^1].Args[1]);
            Assert.Equal("ok", (await devtools.CloseTargetAsync("s", "p")).Message);
            Assert.Equal("p", calls[^1].Args[1]);
        }
        Assert.Equal(0, interop.LiveHandleCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Errors_PropagateAndReleaseTemporaryHandles(bool synchronous)
    {
        var (interop, runtime) = FakeWorld.Create();
        var binding = new FakeObject
        {
            ["acquire"] = new FakeFunction((_, _) => synchronous
                ? throw FakeJsThrow.WithMessage("session limit")
                : FakePromise.Rejected(new FakeObject { ["message"] = "session limit" })),
        };
        using (var env = FakeWorld.CreateEnv(runtime, interop, new FakeObject { ["BROWSER"] = binding }))
        using (var browser = env.Browser("BROWSER"))
        {
            var error = await Assert.ThrowsAsync<JsException>(() => browser.AcquireAsync(new() { Targets = true }));
            Assert.Contains("session limit", error.Message);
        }
        Assert.Equal(0, interop.LiveHandleCount);
    }

    private static FakeObject Parse(JsRuntime runtime, FakeJsInterop interop, string json)
    {
        using var js = runtime.JsonParse(json);
        return Assert.IsType<FakeObject>(interop.Get(js.Handle));
    }
}
