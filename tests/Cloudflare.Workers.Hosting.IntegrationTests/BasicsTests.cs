using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Cloudflare.Workers.Hosting.IntegrationTests;

/// <summary>Strings, bytes, headers, JSON, env and error propagation across the wasm boundary.</summary>
public class BasicsTests(WorkerFixture worker) : IntegrationTestBase(worker)
{
    [Fact]
    public async Task Ping_ReturnsPong()
    {
        Assert.Equal("pong", await Client.GetStringAsync("ping", Ct));
    }

    [Fact]
    public async Task UnknownRoute_Returns404()
    {
        using var response = await Client.GetAsync("does-not-exist", Ct);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Unicode_ResponseIsDecodedCorrectly()
    {
        Assert.Equal("こんにちは 🌏 café", await Client.GetStringAsync("unicode", Ct));
    }

    [Fact]
    public async Task EchoText_RoundTripsUnicode()
    {
        const string text = "日本語 / emoji 😀🚀 / ñ / \0 nul / \r\n";
        using var response = await Client.PostAsync("echo/text", Text(text), Ct);
        Assert.Equal(text, await response.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task EchoText_EmptyBody()
    {
        using var response = await Client.PostAsync("echo/text", Text(string.Empty), Ct);
        Assert.Equal(string.Empty, await response.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task EchoText_LargeBody()
    {
        string text = new string('a', 1_000_000) + "終";
        using var response = await Client.PostAsync("echo/text", Text(text), Ct);
        Assert.Equal(text, await response.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task EchoBytes_AllByteValuesSurvive()
    {
        var bytes = Enumerable.Range(0, 256).Select(i => (byte)i).ToArray();
        using var response = await Client.PostAsync("echo/bytes", new ByteArrayContent(bytes), Ct);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync(Ct));
        Assert.Equal(bytes, await response.Content.ReadAsByteArrayAsync(Ct));
    }

    [Fact]
    public async Task EchoBytes_LargeRandomBody()
    {
        var bytes = new byte[2 * 1024 * 1024];
        new Random(42).NextBytes(bytes);
        using var response = await Client.PostAsync("echo/bytes", new ByteArrayContent(bytes), Ct);
        Assert.Equal(bytes, await response.Content.ReadAsByteArrayAsync(Ct));
    }

    [Fact]
    public async Task EchoBytes_EmptyBody()
    {
        using var response = await Client.PostAsync("echo/bytes", new ByteArrayContent([]), Ct);
        Assert.Empty(await response.Content.ReadAsByteArrayAsync(Ct));
    }

    [Fact]
    public async Task Bytes_GeneratedByWorker()
    {
        var bytes = await Client.GetByteArrayAsync("bytes/100000", Ct);
        Assert.Equal(100_000, bytes.Length);
        Assert.All(bytes.Select((b, i) => (b, i)), t => Assert.Equal((byte)t.i, t.b));

        Assert.Empty(await Client.GetByteArrayAsync("bytes/0", Ct));
    }

    [Fact]
    public async Task Json_IsDeserializedAndSerializedByTheWorker()
    {
        using var response = await Client.PostAsJsonAsync("echo/json", new { name = "Ada", age = 36 }, Ct);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.Equal("Ada", json.GetProperty("name").GetString());
        Assert.Equal(37, json.GetProperty("age").GetInt32());
    }

    [Fact]
    public async Task RequestHeaders_AreVisibleToTheWorker()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "headers");
        request.Headers.Add("X-Custom-One", "1");
        request.Headers.Add("X-Custom-Two", "two");
        using var response = await Client.SendAsync(request, Ct);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);

        var headers = json.EnumerateArray().ToDictionary(
            e => e.GetProperty("name").GetString()!,
            e => e.GetProperty("value").GetString()!,
            StringComparer.OrdinalIgnoreCase);
        Assert.Equal("1", headers["x-custom-one"]);
        Assert.Equal("two", headers["x-custom-two"]);
    }

    [Fact]
    public async Task ResponseHeaders_AndStatusAreApplied()
    {
        using var response = await Client.GetAsync("response-headers", Ct);
        Assert.Equal(218, (int)response.StatusCode);
        Assert.Equal("one", response.Headers.GetValues("x-first").Single());
        Assert.Equal("two", response.Headers.GetValues("x-second").Single());
        Assert.Equal("a, b", string.Join(", ", response.Headers.GetValues("x-multi")));
    }

    [Fact]
    public async Task Redirect_IsReturnedWithLocation()
    {
        using var response = await Client.GetAsync("redirect", Ct);
        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        Assert.Equal("/ping", response.Headers.Location?.OriginalString);
    }

    [Fact]
    public async Task Env_ReadsVarsAndBindings()
    {
        var json = await GetJsonAsync("env");
        Assert.Equal("value from wrangler.jsonc", json.GetProperty("var").GetString());
        Assert.Equal(JsonValueKind.Null, json.GetProperty("missing").ValueKind);
        Assert.True(json.GetProperty("hasKv").GetBoolean());
        Assert.False(json.GetProperty("hasNope").GetBoolean());
    }

    [Fact]
    public async Task Env_MissingBindingThrows()
    {
        using var response = await Client.GetAsync("env/missing-kv", Ct);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Contains("INTEGRATION_TEST_NOPE", await response.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task HandlerException_BecomesAnErrorResponse_AndWorkerKeepsServing()
    {
        using var thrown = await Client.GetAsync("throw", Ct);
        Assert.False(thrown.IsSuccessStatusCode);

        using var thrownAsync = await Client.GetAsync("throw-async", Ct);
        Assert.False(thrownAsync.IsSuccessStatusCode);

        Assert.Equal("pong", await Client.GetStringAsync("ping", Ct));
    }
}
