using System.Net;
using System.Text;
using System.Text.Json;

namespace Cloudflare.Workers.Hosting.IntegrationTests;

public class KvTests(WorkerFixture worker) : E2ETestBase(worker)
{
    [Fact]
    public async Task PutGetDelete_Text()
    {
        string key = UniqueKey();

        using var missing = await Client.GetAsync($"kv/{key}", Ct);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);

        using var put = await Client.PutAsync($"kv/{key}", Text("héllo 🌏"), Ct);
        Assert.True(put.IsSuccessStatusCode);
        Assert.Equal("héllo 🌏", await Client.GetStringAsync($"kv/{key}", Ct));

        using var delete = await Client.DeleteAsync($"kv/{key}", Ct);
        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);

        using var gone = await Client.GetAsync($"kv/{key}", Ct);
        Assert.Equal(HttpStatusCode.NotFound, gone.StatusCode);
    }

    [Fact]
    public async Task PutGet_Bytes()
    {
        string key = UniqueKey();
        var bytes = Enumerable.Range(0, 256).Select(i => (byte)i).ToArray();

        using var put = await Client.PutAsync($"kv-bytes/{key}", new ByteArrayContent(bytes), Ct);
        Assert.True(put.IsSuccessStatusCode);
        Assert.Equal(bytes, await Client.GetByteArrayAsync($"kv-bytes/{key}", Ct));
    }

    [Fact]
    public async Task List_FiltersByPrefix_AndPaginates()
    {
        string prefix = UniqueKey("list") + "-";
        for (int i = 0; i < 5; i++)
        {
            using var put = await Client.PutAsync($"kv/{prefix}{i}", Text("v"), Ct);
            Assert.True(put.IsSuccessStatusCode);
        }

        var all = await GetJsonAsync($"kv-list?prefix={Uri.EscapeDataString(prefix)}");
        Assert.Equal(
            Enumerable.Range(0, 5).Select(i => $"{prefix}{i}"),
            all.GetProperty("keys").EnumerateArray().Select(k => k.GetString()));
        Assert.True(all.GetProperty("listComplete").GetBoolean());

        var seen = new List<string>();
        string? cursor = null;
        int pages = 0;
        do
        {
            string url = $"kv-list?prefix={Uri.EscapeDataString(prefix)}&limit=2" + (cursor is null ? "" : $"&cursor={Uri.EscapeDataString(cursor)}");
            var page = await GetJsonAsync(url);
            seen.AddRange(page.GetProperty("keys").EnumerateArray().Select(k => k.GetString()!));
            cursor = page.GetProperty("cursor").GetString();
            pages++;
        }
        while (cursor is not null && pages < 10);

        Assert.Equal(3, pages);
        Assert.Equal(Enumerable.Range(0, 5).Select(i => $"{prefix}{i}"), seen);
    }

    [Fact]
    public async Task ExpirationTtl_IsReportedByList()
    {
        string key = UniqueKey("ttl");
        using var put = await Client.PutAsync($"kv/{key}?ttl=3600", Text("v"), Ct);
        Assert.True(put.IsSuccessStatusCode);

        var list = await GetJsonAsync($"kv-list?prefix={key}");
        Assert.True(list.GetProperty("hasExpiration").GetBoolean());
    }
}

public class R2Tests(WorkerFixture worker) : E2ETestBase(worker)
{
    [Fact]
    public async Task PutGetHeadDelete()
    {
        string key = UniqueKey("obj");
        var bytes = Encoding.UTF8.GetBytes("r2 payload 🌏");

        using var missing = await Client.GetAsync($"r2/{key}", Ct);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);

        using var put = await Client.PutAsync($"r2/{key}", new ByteArrayContent(bytes), Ct);
        var putInfo = JsonDocument.Parse(await put.Content.ReadAsStringAsync(Ct)).RootElement;
        Assert.Equal(key, putInfo.GetProperty("key").GetString());
        Assert.Equal(bytes.Length, putInfo.GetProperty("size").GetInt64());
        Assert.False(string.IsNullOrEmpty(putInfo.GetProperty("etag").GetString()));

        Assert.Equal(bytes, await Client.GetByteArrayAsync($"r2/{key}", Ct));
        Assert.Equal("r2 payload 🌏", await Client.GetStringAsync($"r2/{key}/text", Ct));

        var head = await GetJsonAsync($"r2/{key}/head");
        Assert.Equal(bytes.Length, head.GetProperty("size").GetInt64());
        Assert.Equal(putInfo.GetProperty("etag").GetString(), head.GetProperty("etag").GetString());

        using var delete = await Client.DeleteAsync($"r2/{key}", Ct);
        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);

        using var gone = await Client.GetAsync($"r2/{key}/head", Ct);
        Assert.Equal(HttpStatusCode.NotFound, gone.StatusCode);
    }

    [Fact]
    public async Task LargeObject_RoundTrips()
    {
        string key = UniqueKey("big");
        var bytes = new byte[3 * 1024 * 1024];
        new Random(7).NextBytes(bytes);

        using var put = await Client.PutAsync($"r2/{key}", new ByteArrayContent(bytes), Ct);
        Assert.True(put.IsSuccessStatusCode);
        Assert.Equal(bytes, await Client.GetByteArrayAsync($"r2/{key}", Ct));
    }
}

public class D1Tests(WorkerFixture worker) : E2ETestBase(worker)
{
    // The tests share one table; run them one after another.
    private static readonly SemaphoreSlim TableLock = new(1, 1);

    private async Task ResetAsync()
    {
        using var response = await Client.PostAsync("d1/reset", null, Ct);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task InsertAndQuery_UsesBoundParametersAndReturnsTypedRows()
    {
        await TableLock.WaitAsync(Ct);
        try
        {
            await ResetAsync();

            var first = await SendJsonAsync(HttpMethod.Post, "d1/items", Text("""{"name":"apple 🍎","price":1.5,"note":"fresh"}""", "application/json"));
            Assert.True(first.GetProperty("success").GetBoolean());
            Assert.Equal(1, first.GetProperty("changes").GetInt64());
            long firstId = first.GetProperty("lastRowId").GetInt64();

            var second = await SendJsonAsync(HttpMethod.Post, "d1/items", Text("""{"name":"pear","price":2,"note":null}""", "application/json"));
            long secondId = second.GetProperty("lastRowId").GetInt64();
            Assert.Equal(firstId + 1, secondId);

            var all = await GetJsonAsync("d1/items");
            Assert.Equal(2, all.GetArrayLength());
            Assert.Equal("apple 🍎", all[0].GetProperty("name").GetString());
            Assert.Equal(1.5, all[0].GetProperty("price").GetDouble());
            Assert.Equal(JsonValueKind.Null, all[1].GetProperty("note").ValueKind);

            var typed = await GetJsonAsync("d1/items/typed");
            Assert.Equal(2, typed.GetArrayLength());
            Assert.Equal("pear", typed[1].GetProperty("name").GetString());
            Assert.Equal(2, typed[1].GetProperty("price").GetDouble());

            var one = await GetJsonAsync($"d1/items/{secondId}");
            Assert.Equal("pear", one.GetProperty("name").GetString());

            using var missing = await Client.GetAsync("d1/items/999999", Ct);
            Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        }
        finally
        {
            TableLock.Release();
        }
    }

    [Fact]
    public async Task InvalidSql_SurfacesAsAnException()
    {
        using var response = await Client.GetAsync("d1/invalid", Ct);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("no_such_table", await response.Content.ReadAsStringAsync(Ct));
    }
}

public class ServiceBindingTests(WorkerFixture worker) : E2ETestBase(worker)
{
    [Fact]
    public async Task FetchAsync_CallsAnotherWorker()
    {
        using var response = await Client.PostAsync("service/echo", Text("hello upstream 🌏"), Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct)).RootElement;
        Assert.Equal("upstream", json.GetProperty("worker").GetString());
        Assert.Equal("POST", json.GetProperty("method").GetString());
        Assert.Equal("/echo", json.GetProperty("path").GetString());
        Assert.Equal("hello upstream 🌏", json.GetProperty("body").GetString());
    }
}
