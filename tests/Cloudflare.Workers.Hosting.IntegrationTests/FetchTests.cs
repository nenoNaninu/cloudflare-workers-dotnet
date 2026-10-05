using System.Net;
using System.Text.Json;

namespace Cloudflare.Workers.Hosting.IntegrationTests;

/// <summary>Outbound requests through the <c>Fetch</c> class.</summary>
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
