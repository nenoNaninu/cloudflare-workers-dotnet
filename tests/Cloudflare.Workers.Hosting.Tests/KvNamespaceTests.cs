using System.Text;
using Cloudflare.Workers.Hosting.Tests.Fakes;
using Xunit;

namespace Cloudflare.Workers.Hosting.Tests;

public class KvNamespaceTests
{
    private static (KvNamespace Kv, Dictionary<string, object?> Store, FakeJsInterop Interop) CreateKv()
    {
        var (interop, runtime) = FakeWorld.Create();
        var store = new Dictionary<string, object?>();
        var kv = new KvNamespace(runtime.WrapToJsObject(interop.Retain(FakeWorld.CreateKv(store))));
        return (kv, store, interop);
    }

    [Fact]
    public async Task GetTextAsync_ReturnsValue()
    {
        var setup = CreateKv();
        using var kv = setup.Kv;
        var store = setup.Store;
        store["greeting"] = "hello";

        Assert.Equal("hello", await kv.GetTextAsync("greeting"));
    }

    [Fact]
    public async Task GetTextAsync_ReturnsNullForMissingKey()
    {
        var setup = CreateKv();
        using var kv = setup.Kv;

        Assert.Null(await kv.GetTextAsync("missing"));
    }

    [Fact]
    public async Task GetBytesAsync_ReturnsBinaryValue()
    {
        var setup = CreateKv();
        using var kv = setup.Kv;
        var store = setup.Store;
        store["blob"] = "abc";

        Assert.Equal(Encoding.UTF8.GetBytes("abc"), await kv.GetBytesAsync("blob"));
    }

    [Fact]
    public async Task PutAsync_StoresValue()
    {
        var setup = CreateKv();
        using var kv = setup.Kv;
        var store = setup.Store;

        await kv.PutAsync("key", "value");

        Assert.Equal("value", store["key"]);
    }

    [Fact]
    public async Task PutAsync_PassesExpirationTtlInSeconds()
    {
        var setup = CreateKv();
        using var kv = setup.Kv;
        var store = setup.Store;

        await kv.PutAsync("key", "value", new KvPutOptions { ExpirationTtl = TimeSpan.FromMinutes(5) });

        var options = Assert.IsType<FakeObject>(store["__lastPutOptions"]);
        Assert.Equal(300d, options["expirationTtl"]);
    }

    [Fact]
    public async Task DeleteAsync_RemovesKey()
    {
        var setup = CreateKv();
        using var kv = setup.Kv;
        var store = setup.Store;
        store["key"] = "value";

        await kv.DeleteAsync("key");

        Assert.False(store.ContainsKey("key"));
    }

    [Fact]
    public async Task ListAsync_ReturnsKeysFilteredByPrefix()
    {
        var setup = CreateKv();
        using var kv = setup.Kv;
        var store = setup.Store;
        store["user:1"] = "a";
        store["user:2"] = "b";
        store["other"] = "c";

        var result = await kv.ListAsync(new KvListOptions { Prefix = "user:" });

        Assert.True(result.ListComplete);
        Assert.Equal(["user:1", "user:2"], result.Keys.Select(k => k.Name));
    }
}
