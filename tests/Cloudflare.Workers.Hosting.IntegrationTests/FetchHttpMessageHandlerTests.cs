using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Cloudflare.Workers.Hosting.IntegrationTests;

/// <summary>HttpClient requests sent through FetchHttpMessageHandler in the real Workers runtime.</summary>
public class FetchHttpMessageHandlerTests(WorkerFixture worker) : IntegrationTestBase(worker)
{
    [Fact]
    public async Task HttpClient_PostsUnicodeBodyAndRequestAndContentHeaders()
    {
        const string body = "HttpClient body こんにちは 🌏 café";
        var json = await SendJsonAsync(HttpMethod.Post, "http-client/echo", Text(body));

        Assert.Equal("POST", json.GetProperty("method").GetString());
        Assert.Equal("/echo", json.GetProperty("path").GetString());
        Assert.Equal("?from=http-client", json.GetProperty("query").GetString());
        Assert.Equal(body, json.GetProperty("body").GetString());
        var headers = json.GetProperty("headers");
        Assert.Equal("http-client", headers.GetProperty("x-integration-test").GetString());
        Assert.Equal("a, b", headers.GetProperty("x-multi").GetString());
        Assert.Equal("one, two", headers.GetProperty("x-content").GetString());
        Assert.Equal("text/plain; charset=utf-8", headers.GetProperty("content-type").GetString());
        Assert.Equal(Encoding.UTF8.GetByteCount(body).ToString(), headers.GetProperty("content-length").GetString());
    }

    [Fact]
    public async Task HttpClient_PostsBinaryBodyWithoutChangingBytes()
    {
        var body = Enumerable.Range(0, 256).Select(i => (byte)i).ToArray();
        using var content = new ByteArrayContent(body);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");

        var json = await SendJsonAsync(HttpMethod.Post, "http-client/echo", content);

        Assert.Equal(Convert.ToBase64String(body), json.GetProperty("bodyBase64").GetString());
        Assert.Equal("application/octet-stream", json.GetProperty("headers").GetProperty("content-type").GetString());
        Assert.Equal("256", json.GetProperty("headers").GetProperty("content-length").GetString());
    }

    [Theory]
    [InlineData("GET")]
    [InlineData("HEAD")]
    public async Task HttpClient_SendsEmptyGetAndHeadContentWithoutFetchRejectingIt(string method)
    {
        var json = await GetJsonAsync($"http-client/empty/{method}");

        Assert.Equal(200, json.GetProperty("status").GetInt32());
        Assert.Equal("yes", json.GetProperty("upstream").GetString());
        Assert.Equal(Worker.UpstreamUrl + "/echo", json.GetProperty("requestUrl").GetString());
        Assert.True(json.GetProperty("sameRequest").GetBoolean());
        var body = Convert.FromBase64String(json.GetProperty("bodyBase64").GetString()!);
        if (method == "HEAD")
        {
            Assert.Empty(body);
            Assert.Equal(0, json.GetProperty("contentLength").GetInt64());
        }
        else
        {
            using var echo = JsonDocument.Parse(body);
            Assert.Equal("GET", echo.RootElement.GetProperty("method").GetString());
            Assert.Equal(string.Empty, echo.RootElement.GetProperty("body").GetString());
        }
    }

    [Fact]
    public async Task HttpClient_ReadsResponseAndContentHeadersAndRetainsRequest()
    {
        var json = await GetJsonAsync("http-client/response/headers?metadata=true");

        Assert.Equal(200, json.GetProperty("status").GetInt32());
        Assert.Equal("yes", json.GetProperty("upstream").GetString());
        Assert.Equal("a, b", json.GetProperty("multi").GetString());
        Assert.StartsWith("text/plain", json.GetProperty("contentType").GetString());
        Assert.Equal("headers", Encoding.UTF8.GetString(Convert.FromBase64String(json.GetProperty("bodyBase64").GetString()!)));
        Assert.Equal(7, json.GetProperty("contentLength").GetInt64());
        Assert.Equal(Worker.UpstreamUrl + "/headers", json.GetProperty("requestUrl").GetString());
        Assert.True(json.GetProperty("sameRequest").GetBoolean());
    }

    [Fact]
    public async Task HttpClient_ReadsBinaryResponse()
    {
        using var response = await Client.GetAsync("http-client/response/bytes", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/octet-stream", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(Enumerable.Range(0, 256).Select(i => (byte)i), await response.Content.ReadAsByteArrayAsync(Ct));
    }

    [Fact]
    public async Task HttpClient_ReadsJsonResponse()
    {
        var person = await GetJsonAsync("http-client/response/json");

        Assert.Equal("Ada", person.GetProperty("name").GetString());
        Assert.Equal(36, person.GetProperty("age").GetInt32());
    }

    [Theory]
    [InlineData("not-found", HttpStatusCode.NotFound, "missing upstream resource")]
    [InlineData("no-content", HttpStatusCode.NoContent, "")]
    public async Task HttpClient_PreservesErrorAndNoContentResponses(string endpoint, HttpStatusCode status, string body)
    {
        using var response = await Client.GetAsync($"http-client/response/{endpoint}", Ct);

        Assert.Equal(status, response.StatusCode);
        Assert.Equal(body, await response.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task HttpClient_DecodesGzipAndDropsWireEncodingAndLength()
    {
        var json = await GetJsonAsync("http-client/response/gzip?metadata=true");
        var body = Convert.FromBase64String(json.GetProperty("bodyBase64").GetString()!);

        Assert.Equal(200, json.GetProperty("status").GetInt32());
        Assert.Equal(UpstreamServer.CompressedResponseBody, Encoding.UTF8.GetString(body));
        Assert.Equal("text/plain; charset=utf-8", json.GetProperty("contentType").GetString());
        Assert.Equal(body.Length, json.GetProperty("contentLength").GetInt64());
        Assert.Empty(json.GetProperty("contentEncoding").EnumerateArray());
    }

    [Fact]
    public async Task HttpClient_FollowsRedirects()
    {
        var json = await GetJsonAsync("http-client/redirect/follow");

        Assert.Equal("GET", json.GetProperty("method").GetString());
        Assert.Equal("/echo", json.GetProperty("path").GetString());
        Assert.Equal("?redirected=1", json.GetProperty("query").GetString());
    }

    [Fact]
    public async Task HttpClient_ManualRedirectReturnsStatusAndLocation()
    {
        using var response = await Client.GetAsync("http-client/redirect/manual", Ct);

        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        Assert.Equal("/echo?redirected=1", response.Headers.Location?.OriginalString);
    }

    [Fact]
    public async Task HttpClient_NetworkFailureThrowsJsException()
    {
        using var response = await Client.GetAsync("http-client/unreachable", Ct);

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.NotEmpty(await response.Content.ReadAsStringAsync(Ct));
    }
}
