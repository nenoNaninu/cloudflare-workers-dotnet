using System.Net;
using System.Text.Json;

namespace Cloudflare.Workers.Hosting.IntegrationTests;

/// <summary>Outbound requests: the <c>Fetch</c> class and <c>HttpClient</c> over <c>FetchHttpMessageHandler</c>.</summary>
public class FetchTests(WorkerFixture worker) : E2ETestBase(worker)
{
    [E2EFact]
    public async Task Fetch_PostsBodyAndHeaders()
    {
        using var response = await Client.PostAsync("fetch/echo", Text("fetch body 🌏"), Ct);
        var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct)).RootElement;

        Assert.Equal("POST", json.GetProperty("method").GetString());
        Assert.Equal("/echo", json.GetProperty("path").GetString());
        Assert.Equal("?from=fetch", json.GetProperty("query").GetString());
        Assert.Equal("fetch body 🌏", json.GetProperty("body").GetString());
        Assert.Equal("fetch", json.GetProperty("headers").GetProperty("x-e2e").GetString());
    }

    [E2EFact]
    public async Task Fetch_ReadsResponseHeaders()
    {
        var json = await GetJsonAsync("fetch/headers");
        Assert.Equal(200, json.GetProperty("status").GetInt32());
        Assert.Equal("yes", json.GetProperty("upstream").GetString());
        Assert.Equal("a, b", json.GetProperty("multi").GetString());
        Assert.StartsWith("text/plain", json.GetProperty("contentType").GetString());
    }

    [E2EFact]
    public async Task Fetch_ReadsBinaryAndJsonBodies()
    {
        Assert.Equal(
            Enumerable.Range(0, 256).Select(i => (byte)i),
            await Client.GetByteArrayAsync("fetch/bytes", Ct));

        var person = await GetJsonAsync("fetch/json");
        Assert.Equal("Ada", person.GetProperty("name").GetString());
        Assert.Equal(36, person.GetProperty("age").GetInt32());
    }

    [E2EFact]
    public async Task Fetch_RedirectModes()
    {
        // Follow: the runtime lands on /echo?redirected=1
        var followed = await Client.GetStringAsync("fetch/redirect/follow", Ct);
        Assert.StartsWith("200|", followed);

        // Manual: the 302 is surfaced together with its Location header.
        var manual = await Client.GetStringAsync("fetch/redirect/manual", Ct);
        Assert.StartsWith("302|", manual);
        Assert.Contains("/echo", manual);

        // Error: fetch rejects, which surfaces as a JsException.
        using var error = await Client.GetAsync("fetch/redirect/error", Ct);
        Assert.Equal(HttpStatusCode.BadGateway, error.StatusCode);
    }

    [E2EFact]
    public async Task Fetch_NetworkFailure_ThrowsJsException()
    {
        using var response = await Client.GetAsync("fetch/unreachable", Ct);
        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
    }
}

public class HttpClientTests(WorkerFixture worker) : E2ETestBase(worker)
{
    [E2EFact]
    public async Task Get_SendsDefaultHeadersAndReadsTheResponse()
    {
        var result = await GetJsonAsync("http-client/get");
        Assert.Equal(200, result.GetProperty("status").GetInt32());
        Assert.Equal("application/json", result.GetProperty("contentType").GetString());
        Assert.Equal("yes", result.GetProperty("header").GetString());

        var echoed = JsonDocument.Parse(result.GetProperty("body").GetString()!).RootElement;
        Assert.Equal("GET", echoed.GetProperty("method").GetString());
        Assert.Equal("?from=http-client", echoed.GetProperty("query").GetString());
        Assert.Equal("http-client", echoed.GetProperty("headers").GetProperty("x-e2e").GetString());
    }

    [E2EFact]
    public async Task Post_SendsBodyAndContentType()
    {
        var result = await SendJsonAsync(HttpMethod.Post, "http-client/post", Text("""{"msg":"こんにちは"}""", "application/json"));
        Assert.Equal(200, result.GetProperty("status").GetInt32());

        var echoed = JsonDocument.Parse(result.GetProperty("body").GetString()!).RootElement;
        Assert.Equal("POST", echoed.GetProperty("method").GetString());
        Assert.Equal("""{"msg":"こんにちは"}""", echoed.GetProperty("body").GetString());
        Assert.StartsWith("application/json", echoed.GetProperty("headers").GetProperty("content-type").GetString());
    }

    [E2EFact]
    public async Task ErrorStatusCodes_AreReturnedNotThrown()
    {
        foreach (int code in new[] { 201, 400, 404, 500, 503 })
        {
            var result = await GetJsonAsync($"http-client/status/{code}");
            Assert.Equal(code, result.GetProperty("status").GetInt32());
            Assert.Equal($"status {code}", result.GetProperty("body").GetString());
        }
    }

    [E2EFact]
    public async Task AllowAutoRedirect_ControlsRedirectHandling()
    {
        var followed = await GetJsonAsync("http-client/redirect/true");
        Assert.Equal(200, followed.GetProperty("status").GetInt32());
        Assert.Contains("redirected=1", followed.GetProperty("body").GetString());

        var notFollowed = await GetJsonAsync("http-client/redirect/false");
        Assert.Equal(302, notFollowed.GetProperty("status").GetInt32());
        Assert.Contains("/echo", notFollowed.GetProperty("header").GetString());
    }

    [E2EFact]
    public async Task BinaryBody_IsReadIntact()
    {
        Assert.Equal(
            Enumerable.Range(0, 256).Select(i => (byte)i),
            await Client.GetByteArrayAsync("http-client/bytes", Ct));
    }

    [E2EFact]
    public async Task ContentEncoding_IsDecodedByTheRuntime()
    {
        Assert.Equal("compressed payload", await Client.GetStringAsync("http-client/gzip", Ct));
    }

    [E2EFact]
    public async Task ParallelRequests_AllComplete()
    {
        Assert.Equal(
            string.Join(",", Enumerable.Range(0, 8).Select(i => $"text-{i}")),
            await Client.GetStringAsync("http-client/parallel", Ct));
    }

    [E2EFact]
    public async Task NetworkFailure_IsThrownToTheCaller()
    {
        using var response = await Client.GetAsync("http-client/unreachable", Ct);
        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
    }

    [E2EFact]
    public async Task CancelledToken_ThrowsOperationCanceled()
    {
        Assert.Equal("cancelled", await Client.GetStringAsync("http-client/cancelled", Ct));
    }
}
