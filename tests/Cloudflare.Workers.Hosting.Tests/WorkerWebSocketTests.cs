using Cloudflare.Workers.Hosting.Interop;
using Cloudflare.Workers.Hosting.Tests.Fakes;
using Xunit;

namespace Cloudflare.Workers.Hosting.Tests;

public class WorkerWebSocketTests
{
    [Fact]
    public async Task UpgradeResponse_SocketSurvivesResponseDisposalAndSupportsTextAndBinary()
    {
        var (interop, runtime) = FakeWorld.Create();
        var socket = new FakeSocket();
        var response = CreateResponse(runtime, interop, socket);
        using (var webSocket = response.GetWebSocket()!)
        {
            response.Dispose();
            Assert.Same(socket.Object, interop.Get(webSocket.Js.Handle));
            webSocket.Accept();
            webSocket.Send("hello");
            webSocket.Send(new byte[] { 1, 2, 3 });
            var receive = webSocket.ReceiveAsync();
            Assert.False(receive.IsCompleted);
            socket.Emit("message", new FakeObject { ["data"] = "reply" });
            using var message = await receive;
            Assert.Equal("reply", message.AsString());
            webSocket.Close(1000, "done");
            Assert.Equal(new[] { "accept", "send", "send", "close" }, socket.Calls.Select(c => c.Method));
            Assert.Equal("hello", Assert.Single(socket.Calls[1].Args));
            Assert.Equal(new byte[] { 1, 2, 3 }, Assert.IsType<byte[]>(Assert.Single(socket.Calls[2].Args)));
            Assert.Equal(new object?[] { 1000d, "done" }, socket.Calls[3].Args);
            Assert.Empty(socket.Listeners);
            webSocket.Dispose();
            Assert.Throws<ObjectDisposedException>(() => webSocket.Accept());
            Assert.Throws<ObjectDisposedException>(() => webSocket.Send("late"));
            await Assert.ThrowsAsync<ObjectDisposedException>(() => webSocket.ReceiveAsync());
        }
        Assert.Single(socket.Calls, c => c.Method == "close");
        Assert.Equal(0, interop.LiveHandleCount);
    }

    [Fact]
    public async Task Receive_BuffersEarlyTextAndBinaryMessages()
    {
        var (interop, runtime) = FakeWorld.Create();
        var socket = new FakeSocket();
        socket.OnAccept = () =>
        {
            socket.Emit("message", new FakeObject { ["data"] = "early" });
            socket.EmitBinary(new byte[] { 1, 2 });
        };
        using (var response = CreateResponse(runtime, interop, socket))
        using (var webSocket = response.GetWebSocket()!)
        {
            webSocket.Accept();
            using var first = await webSocket.ReceiveAsync();
            using var second = await webSocket.ReceiveAsync();
            Assert.Equal("early", first.AsString());
            Assert.Equal(new byte[] { 1, 2 }, second.AsBytes());
        }
        Assert.Empty(socket.Listeners);
        Assert.Equal(0, interop.LiveHandleCount);
    }

    [Fact]
    public async Task Receive_CompletesPendingReadsInOrder()
    {
        var (interop, runtime) = FakeWorld.Create();
        var socket = new FakeSocket();
        using (var response = CreateResponse(runtime, interop, socket))
        using (var webSocket = response.GetWebSocket()!)
        {
            var first = webSocket.ReceiveAsync();
            var second = webSocket.ReceiveAsync();
            socket.Emit("message", new FakeObject { ["data"] = "one" });
            socket.Emit("message", new FakeObject { ["data"] = "two" });
            using var one = await first;
            using var two = await second;
            Assert.Equal("one", one.AsString());
            Assert.Equal("two", two.AsString());
        }
        Assert.Equal(0, interop.LiveHandleCount);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RemoteClose_DrainsMessagesThenThrowsWithCloseDetails(bool autoReplyToClose)
    {
        var (interop, runtime) = FakeWorld.Create();
        var socket = new FakeSocket { AutoReplyToClose = autoReplyToClose };
        using (var response = CreateResponse(runtime, interop, socket))
        using (var webSocket = response.GetWebSocket()!)
        {
            socket.Emit("message", new FakeObject { ["data"] = "last" });
            socket.Emit("close", new FakeObject { ["code"] = 1001d, ["reason"] = "going away" });
            using var last = await webSocket.ReceiveAsync();
            Assert.Equal("last", last.AsString());
            var error = await Assert.ThrowsAsync<JsException>(() => webSocket.ReceiveAsync());
            Assert.Contains("1001", error.Message);
            Assert.Contains("going away", error.Message);
            Assert.Empty(socket.Listeners);
            Assert.Equal(3d, socket.Object["readyState"]);
            if (autoReplyToClose)
            {
                Assert.DoesNotContain(socket.Calls, c => c.Method == "close");
            }
            else
            {
                Assert.Empty(Assert.Single(socket.Calls, c => c.Method == "close").Args);
            }
        }
        Assert.Equal(0, interop.LiveHandleCount);
    }

    [Fact]
    public async Task RemoteClose_WithoutStatusCompletesManualHandshake()
    {
        var (interop, runtime) = FakeWorld.Create();
        var socket = new FakeSocket { AutoReplyToClose = false };
        using (var response = CreateResponse(runtime, interop, socket))
        using (var webSocket = response.GetWebSocket()!)
        {
            var pending = webSocket.ReceiveAsync();
            socket.Emit("close", new FakeObject { ["code"] = 1005d, ["reason"] = string.Empty });
            var error = await Assert.ThrowsAsync<JsException>(() => pending);
            Assert.Contains("1005", error.Message);
            Assert.Equal(3d, socket.Object["readyState"]);
            Assert.Empty(Assert.Single(socket.Calls, c => c.Method == "close").Args);
        }
        Assert.Equal(0, interop.LiveHandleCount);
    }

    [Theory]
    [InlineData("close")]
    [InlineData("error")]
    public async Task TerminalEvent_FailsAllPendingAndFutureReads(string type)
    {
        var (interop, runtime) = FakeWorld.Create();
        var socket = new FakeSocket();
        using (var response = CreateResponse(runtime, interop, socket))
        using (var webSocket = response.GetWebSocket()!)
        {
            var first = webSocket.ReceiveAsync();
            var second = webSocket.ReceiveAsync();
            socket.Emit(type, new FakeObject { ["code"] = 1000d });
            await Assert.ThrowsAsync<JsException>(() => first);
            await Assert.ThrowsAsync<JsException>(() => second);
            await Assert.ThrowsAsync<JsException>(() => webSocket.ReceiveAsync());
            Assert.Empty(socket.Listeners);
        }
        Assert.Equal(0, interop.LiveHandleCount);
    }

    [Fact]
    public async Task Dispose_FailsPendingReadsReleasesQueuedMessagesAndIgnoresLateCallbacks()
    {
        var (interop, runtime) = FakeWorld.Create();
        var socket = new FakeSocket();
        using (var response = CreateResponse(runtime, interop, socket))
        {
            var webSocket = response.GetWebSocket()!;
            var lateMessage = socket.Listeners["message"];
            var pending = webSocket.ReceiveAsync();
            webSocket.Dispose();
            await Assert.ThrowsAsync<JsException>(() => pending);
            lateMessage.Invoke(socket.Object, [new FakeObject { ["data"] = "late" }]);
            webSocket.Dispose();
            Assert.Empty(socket.Listeners);

            using var another = response.GetWebSocket()!;
            socket.Emit("message", new FakeObject { ["data"] = "unread" });
        }
        Assert.Single(socket.Calls, c => c.Method == "close");
        Assert.Equal(0, interop.LiveHandleCount);
    }

    [Fact]
    public void NormalResponse_HasNoSocket()
    {
        var (interop, runtime) = FakeWorld.Create();
        using (var response = new FetchResponseMessage(runtime.WrapToJsObject(interop.Retain(new FakeObject { ["status"] = 200d }))))
        {
            Assert.Null(response.GetWebSocket());
            response.Dispose();
            Assert.Throws<ObjectDisposedException>(() => response.GetWebSocket());
        }
        Assert.Equal(0, interop.LiveHandleCount);
    }

    [Fact]
    public void PartialListenerRegistrationFailure_ReleasesCallbacksAndSocketHandle()
    {
        var (interop, runtime) = FakeWorld.Create();
        var socket = new FakeSocket { FailOnAdd = "close" };
        using (var response = CreateResponse(runtime, interop, socket))
        {
            var error = Assert.Throws<JsException>(() => response.GetWebSocket());
            Assert.Contains("registration failed", error.Message);
            Assert.Empty(socket.Listeners);
            // A JS function retained after failed construction must no longer reach this socket.
            socket.RemovedMessageListener!.Invoke(socket.Object, [new FakeObject { ["data"] = "late" }]);
        }
        Assert.Equal(0, interop.LiveHandleCount);
    }

    private static FetchResponseMessage CreateResponse(JsRuntime runtime, FakeJsInterop interop, FakeSocket socket)
        => new(runtime.WrapToJsObject(interop.Retain(new FakeObject { ["status"] = 101d, ["webSocket"] = socket.Object })));

    private sealed class FakeSocket
    {
        private bool _sentClose;
        private bool _receivedClose;

        public FakeObject Object { get; } = new() { ["readyState"] = 1d, ["binaryType"] = "blob" };
        public Dictionary<string, FakeFunction> Listeners { get; } = [];
        public List<(string Method, object?[] Args)> Calls { get; } = [];
        public string? FailOnAdd { get; init; }
        public bool AutoReplyToClose { get; init; } = true;
        public Action? OnAccept { get; set; }
        public FakeFunction? RemovedMessageListener { get; private set; }

        public FakeSocket()
        {
            Object["addEventListener"] = new FakeFunction((_, args) =>
            {
                string type = (string)args[0]!;
                if (type == FailOnAdd)
                {
                    throw FakeJsThrow.WithMessage("registration failed");
                }
                Listeners.Add(type, Assert.IsType<FakeFunction>(args[1]));
                return JsUndefined.Value;
            });
            Object["removeEventListener"] = new FakeFunction((_, args) =>
            {
                string type = (string)args[0]!;
                Assert.Same(Listeners[type], args[1]);
                if (type == "message")
                {
                    RemovedMessageListener = Listeners[type];
                }
                Listeners.Remove(type);
                return JsUndefined.Value;
            });
            foreach (string method in new[] { "accept", "send", "close" })
            {
                Object[method] = new FakeFunction((receiver, args) =>
                {
                    Assert.Same(Object, receiver);
                    Calls.Add((method, args));
                    if (method == "accept")
                    {
                        OnAccept?.Invoke();
                    }
                    if (method == "close")
                    {
                        _sentClose = true;
                        if (_receivedClose)
                        {
                            Object["readyState"] = 3d;
                        }
                        else
                        {
                            Emit("close", new FakeObject
                            {
                                ["code"] = args.ElementAtOrDefault(0) ?? 1005d,
                                ["reason"] = args.ElementAtOrDefault(1) ?? string.Empty,
                            });
                        }
                    }
                    return JsUndefined.Value;
                });
            }
        }

        public void Emit(string type, FakeObject evt)
        {
            if (type == "close")
            {
                _receivedClose = true;
                Object["readyState"] = AutoReplyToClose || _sentClose ? 3d : 2d;
            }
            if (Listeners.TryGetValue(type, out var listener))
            {
                listener.Invoke(Object, [evt]);
            }
        }

        public void EmitBinary(byte[] bytes)
        {
            object data = Equals(Object["binaryType"], "arraybuffer")
                ? bytes
                : new FakeObject { ["__type"] = "Blob" };
            Emit("message", new FakeObject { ["data"] = data });
        }
    }
}
