using System.Net;
using System.Net.Http.Headers;
using Cloudflare.Workers.Hosting.Interop;
using Cloudflare.Workers.Hosting.Tests.Fakes;
using Xunit;

namespace Cloudflare.Workers.Hosting.Tests;

// Shares the collection used by tests that modify the process-wide JsRuntime.Current.
[Collection("HandlerHostState")]
public class FetchHttpMessageHandlerTests
{
    [Fact]
    public void Constructor_RejectsNullRuntime()
    {
        var exception = Assert.Throws<ArgumentNullException>(() => new FetchHttpMessageHandler(null!));

        Assert.Equal("runtime", exception.ParamName);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("relative/path")]
    public async Task SendAsync_RejectsMissingOrRelativeUriWithoutFetching(string? uri)
    {
        var (interop, runtime) = FakeWorld.Create();
        var captured = new List<object?[]>();
        interop.Globals["fetch"] = new FakeFunction((_, args) =>
        {
            captured.Add(args);
            return FakePromise.Resolved(CreateResponse());
        });
        using var invoker = new HttpMessageInvoker(new FetchHttpMessageHandler(runtime));
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => invoker.SendAsync(request, CancellationToken.None));

        Assert.Contains("absolute URI", exception.Message);
        Assert.Empty(captured);
        Assert.Equal(0, interop.LiveHandleCount);
    }

    [Fact]
    public async Task SendAsync_CanceledTokenDoesNotFetch()
    {
        var (interop, runtime) = FakeWorld.Create();
        var captured = new List<object?[]>();
        interop.Globals["fetch"] = new FakeFunction((_, args) =>
        {
            captured.Add(args);
            return FakePromise.Resolved(CreateResponse());
        });
        using var invoker = new HttpMessageInvoker(new FetchHttpMessageHandler(runtime));
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://example.com/");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => invoker.SendAsync(request, cancellation.Token));

        Assert.Equal(cancellation.Token, exception.CancellationToken);
        Assert.Empty(captured);
        Assert.Equal(0, interop.LiveHandleCount);
    }

    [Theory]
    [InlineData("GET", false)]
    [InlineData("GET", true)]
    [InlineData("HEAD", false)]
    [InlineData("HEAD", true)]
    [InlineData("POST", false)]
    [InlineData("POST", true)]
    public async Task SendAsync_OmitsMissingOrEmptyBody(string method, bool hasContent)
    {
        var (interop, runtime) = FakeWorld.Create();
        var captured = new List<object?[]>();
        interop.Globals["fetch"] = new FakeFunction((_, args) =>
        {
            captured.Add(args);
            return FakePromise.Resolved(CreateResponse());
        });
        using var invoker = new HttpMessageInvoker(new FetchHttpMessageHandler(runtime));
        using var request = new HttpRequestMessage(new HttpMethod(method), "https://example.com/")
        {
            Content = hasContent ? new ByteArrayContent([]) : null,
        };

        using var response = await invoker.SendAsync(request, CancellationToken.None);

        var args = Assert.Single(captured);
        Assert.Equal("https://example.com/", args[0]);
        var init = Assert.IsType<FakeObject>(args[1]);
        Assert.Equal(method, init["method"]);
        Assert.False(init.ContainsKey("body"));
        Assert.False(init.ContainsKey("headers"));
        Assert.Equal(0, interop.LiveHandleCount);
    }

    [Fact]
    public async Task SendAsync_PassesBinaryBodyAndHeadersWithoutContentLength()
    {
        var (interop, runtime) = FakeWorld.Create();
        var captured = new List<object?[]>();
        interop.Globals["fetch"] = new FakeFunction((_, args) =>
        {
            captured.Add(args);
            return FakePromise.Resolved(CreateResponse());
        });
        byte[] body = [0, 128, 255, 42];
        using var invoker = new HttpMessageInvoker(new FetchHttpMessageHandler(runtime));
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.example.com/items?limit=2")
        {
            Content = new ByteArrayContent(body),
        };
        request.Headers.Add("X-Request", ["first", "second"]);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        request.Content.Headers.Add("X-Content", ["one", "two"]);
        request.Content.Headers.ContentLength = body.Length;

        using var response = await invoker.SendAsync(request, CancellationToken.None);

        var args = Assert.Single(captured);
        Assert.Equal("https://api.example.com/items?limit=2", args[0]);
        var init = Assert.IsType<FakeObject>(args[1]);
        Assert.Equal("POST", init["method"]);
        Assert.Equal(body, Assert.IsType<byte[]>(init["body"]));
        var headers = Assert.IsType<FakeArray>(init["headers"])
            .Select(pair => Assert.IsType<FakeArray>(pair))
            .ToDictionary(pair => Assert.IsType<string>(pair[0]), pair => Assert.IsType<string>(pair[1]),
                StringComparer.OrdinalIgnoreCase);
        Assert.Equal(3, headers.Count);
        Assert.Equal("first, second", headers["X-Request"]);
        Assert.Equal("one, two", headers["X-Content"]);
        Assert.Equal("application/octet-stream", headers["Content-Type"]);
        Assert.False(headers.ContainsKey("Content-Length"));
        Assert.Equal(0, interop.LiveHandleCount);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SendAsync_MapsAllowAutoRedirect(bool allowAutoRedirect)
    {
        var (interop, runtime) = FakeWorld.Create();
        var captured = new List<object?[]>();
        interop.Globals["fetch"] = new FakeFunction((_, args) =>
        {
            captured.Add(args);
            return FakePromise.Resolved(CreateResponse());
        });
        using var handler = new FetchHttpMessageHandler(runtime);
        Assert.True(handler.AllowAutoRedirect);
        handler.AllowAutoRedirect = allowAutoRedirect;
        using var invoker = new HttpMessageInvoker(handler, disposeHandler: false);
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://example.com/");

        using var response = await invoker.SendAsync(request, CancellationToken.None);

        var init = Assert.IsType<FakeObject>(Assert.Single(captured)[1]);
        if (allowAutoRedirect)
        {
            Assert.False(init.ContainsKey("redirect"));
        }
        else
        {
            Assert.Equal("manual", init["redirect"]);
        }
    }

    [Theory]
    [InlineData(200)]
    [InlineData(404)]
    [InlineData(204)]
    public async Task SendAsync_ReturnsStatusBodyHeadersAndOriginalRequest(int status)
    {
        var (interop, runtime) = FakeWorld.Create();
        byte[] body = status == 204 ? [] : [0, 128, 255, 42];
        interop.Globals["fetch"] = new FakeFunction((_, _) => FakePromise.Resolved(CreateResponse(
            status, body, ("Content-Type", "application/octet-stream"), ("X-Response", "remote value"))));
        using var invoker = new HttpMessageInvoker(new FetchHttpMessageHandler(runtime));
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://example.com/");

        using var response = await invoker.SendAsync(request, CancellationToken.None);

        Assert.Equal((HttpStatusCode)status, response.StatusCode);
        Assert.Same(request, response.RequestMessage);
        Assert.Equal(body, await response.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken));
        Assert.Equal("remote value", Assert.Single(response.Headers.GetValues("X-Response")));
        Assert.Equal("application/octet-stream", response.Content.Headers.ContentType?.MediaType);
        Assert.Single(response.Headers);
        Assert.Equal(0, interop.LiveHandleCount);
    }

    [Theory]
    [InlineData("Content-Encoding", "Content-Length")]
    [InlineData("content-encoding", "content-length")]
    [InlineData("cOnTeNt-EnCoDiNg", "cOnTeNt-LeNgTh")]
    public async Task SendAsync_DropsWireEncodingAndRecomputesContentLength(string encodingHeader, string lengthHeader)
    {
        var (interop, runtime) = FakeWorld.Create();
        byte[] decodedBody = "decoded response"u8.ToArray();
        interop.Globals["fetch"] = new FakeFunction((_, _) => FakePromise.Resolved(CreateResponse(
            200, decodedBody, (encodingHeader, "gzip"), (lengthHeader, "999"))));
        using var invoker = new HttpMessageInvoker(new FetchHttpMessageHandler(runtime));
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://example.com/");

        using var response = await invoker.SendAsync(request, CancellationToken.None);

        Assert.Equal(decodedBody, await response.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken));
        Assert.Empty(response.Content.Headers.ContentEncoding);
        Assert.Equal(decodedBody.Length, response.Content.Headers.ContentLength);
        Assert.Empty(response.Headers);
        Assert.Equal(0, interop.LiveHandleCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SendAsync_PropagatesFetchOrBodyReadFailureAndReleasesHandles(bool failBodyRead)
    {
        var (interop, runtime) = FakeWorld.Create();
        var error = new FakeObject { ["message"] = "network down" };
        var fetchResponse = CreateResponse();
        fetchResponse["arrayBuffer"] = new FakeFunction((_, _) => FakePromise.Rejected(error));
        interop.Globals["fetch"] = new FakeFunction((_, _) => failBodyRead
            ? FakePromise.Resolved(fetchResponse)
            : FakePromise.Rejected(error));
        using var invoker = new HttpMessageInvoker(new FetchHttpMessageHandler(runtime));
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://example.com/");

        var exception = await Assert.ThrowsAsync<JsException>(
            () => invoker.SendAsync(request, CancellationToken.None));

        Assert.Contains("network down", exception.Message);
        Assert.Equal(0, interop.LiveHandleCount);
    }

    [Fact]
    public async Task SendAsync_DefaultConstructorUsesCurrentRuntime()
    {
        var originalRuntime = JsRuntime.Current;
        var (interop, runtime) = FakeWorld.Create();
        interop.Globals["fetch"] = new FakeFunction((_, _) => FakePromise.Resolved(CreateResponse(201)));
        JsRuntime.SetCurrent(runtime);
        try
        {
            using var client = new HttpClient(new FetchHttpMessageHandler())
            {
                BaseAddress = new Uri("https://example.com/"),
            };

            using var response = await client.GetAsync("items", TestContext.Current.CancellationToken);

            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            Assert.Equal(new Uri("https://example.com/items"), response.RequestMessage?.RequestUri);
            Assert.Equal(0, interop.LiveHandleCount);
        }
        finally
        {
            JsRuntime.SetCurrent(originalRuntime);
        }
    }

    private static FakeObject CreateResponse(int status = 200, byte[]? body = null,
        params (string Name, string Value)[] headers)
        => new()
        {
            ["status"] = (double)status,
            ["arrayBuffer"] = new FakeFunction((_, _) => FakePromise.Resolved(body ?? Array.Empty<byte>())),
            ["headers"] = FakeWorld.CreateHeaders(headers),
        };
}
