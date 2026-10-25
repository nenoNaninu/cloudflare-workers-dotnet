using System.Net;

namespace Cloudflare.Workers.Hosting.IntegrationTests;

/// <summary>Microsoft.Extensions.DependencyInjection running inside the NativeAOT wasm module.</summary>
public class DependencyInjectionTests(WorkerFixture worker) : IntegrationTestBase(worker)
{
    [Fact]
    public async Task ScopedService_ReceivesEnvAndUsesBindings()
    {
        string key = UniqueKey("di");

        var info = await SendJsonAsync(HttpMethod.Put, $"di/kv/{key}", Text("value via DI"));

        Assert.True(info.GetProperty("sameScopedInstance").GetBoolean());
        Assert.Equal("value from wrangler.jsonc", info.GetProperty("var").GetString());
        Assert.Equal("value via DI", await Client.GetStringAsync($"di/kv/{key}", Ct));
    }

    [Fact]
    public async Task SingletonCache_IsReusedWithinRequest()
    {
        var info = await GetJsonAsync($"di/cache/{UniqueKey("di")}");

        Assert.True(info.GetProperty("sameSingletonInstance").GetBoolean());
        Assert.Equal("hello from cache", info.GetProperty("value").GetString());
        Assert.Equal(info.GetProperty("value").GetString(), info.GetProperty("cachedValue").GetString());
    }

    [Fact]
    public async Task ScopedService_MissingKeyReturns404()
    {
        using var response = await Client.GetAsync($"di/kv/{UniqueKey("di")}", Ct);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
