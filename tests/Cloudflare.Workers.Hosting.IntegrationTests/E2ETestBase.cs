using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace Cloudflare.Workers.Hosting.IntegrationTests;

public abstract class E2ETestBase(WorkerFixture worker)
{
    protected WorkerFixture Worker { get; } = worker;

    protected HttpClient Client => Worker.Client;

    protected static CancellationToken Ct => Xunit.TestContext.Current.CancellationToken;

    protected static string UniqueKey(string prefix = "k") => $"{prefix}-{Guid.NewGuid():N}";

    protected static StringContent Text(string value, string mediaType = "text/plain")
        => new(value, Encoding.UTF8, mediaType);

    protected async Task<JsonElement> GetJsonAsync(string path)
    {
        using var response = await Client.GetAsync(path, Ct);
        Assert.True(response.IsSuccessStatusCode, $"GET {path} -> {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync(Ct)}");
        return await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
    }

    protected async Task<JsonElement> SendJsonAsync(HttpMethod method, string path, HttpContent? content = null)
    {
        using var request = new HttpRequestMessage(method, path) { Content = content };
        using var response = await Client.SendAsync(request, Ct);
        Assert.True(response.IsSuccessStatusCode, $"{method} {path} -> {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync(Ct)}");
        return await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
    }

    /// <summary>Polls until <paramref name="probe"/> returns a non-null value (for waitUntil / cron effects).</summary>
    protected static async Task<T> PollAsync<T>(Func<Task<T?>> probe, TimeSpan? timeout = null) where T : class
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(20));
        while (true)
        {
            var value = await probe();
            if (value is not null)
            {
                return value;
            }

            Assert.True(DateTime.UtcNow < deadline, "Timed out waiting for the condition.");
            await Task.Delay(200, Ct);
        }
    }

    protected async Task<string?> TryGetKvAsync(string key)
    {
        using var response = await Client.GetAsync($"kv/{key}", Ct);
        return response.IsSuccessStatusCode ? await response.Content.ReadAsStringAsync(Ct) : null;
    }
}
