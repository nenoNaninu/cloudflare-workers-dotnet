using System.Text;
using System.Text.Json.Serialization;
using Cloudflare.Workers.Hosting.Tests.Fakes;
using Xunit;

namespace Cloudflare.Workers.Hosting.Tests;

public class RequestTests
{
    [Fact]
    public void ExposesMethodUrlAndPath()
    {
        var (interop, runtime) = FakeWorld.Create();
        using var request = HttpRequest.FromJsObject(runtime.WrapToJsObject(interop.Retain(
            FakeWorld.CreateRequest("POST", "https://example.com/users/42?full=true"))));

        Assert.Equal("POST", request.Method);
        Assert.Equal("https://example.com/users/42?full=true", request.Url);
        Assert.Equal("/users/42", request.Path);
    }

    [Fact]
    public void ReadsHeaders()
    {
        var (interop, runtime) = FakeWorld.Create();
        using var request = HttpRequest.FromJsObject(runtime.WrapToJsObject(interop.Retain(
            FakeWorld.CreateRequest("GET", "https://example.com/", headers: [("user-agent", "test-agent")]))));

        Assert.Equal("test-agent", request.Headers.Get("User-Agent"));
    }

    [Fact]
    public async Task TextAsync_ReadsBody()
    {
        var (interop, runtime) = FakeWorld.Create();
        using var request = HttpRequest.FromJsObject(runtime.WrapToJsObject(interop.Retain(
            FakeWorld.CreateRequest("POST", "https://example.com/", body: "hello body"))));

        Assert.Equal("hello body", await request.ReadAsStringAsync());
    }

    [Fact]
    public async Task BytesAsync_ReadsBody()
    {
        var (interop, runtime) = FakeWorld.Create();
        using var request = HttpRequest.FromJsObject(runtime.WrapToJsObject(interop.Retain(
            FakeWorld.CreateRequest("POST", "https://example.com/", body: "abc"))));

        Assert.Equal(Encoding.UTF8.GetBytes("abc"), await request.ReadAsBytesAsync());
    }

    [Fact]
    public async Task JsonAsync_DeserializesWithSourceGeneratedContract()
    {
        var (interop, runtime) = FakeWorld.Create();
        using var request = HttpRequest.FromJsObject(runtime.WrapToJsObject(interop.Retain(
            FakeWorld.CreateRequest("POST", "https://example.com/", body: """{"Name":"neo","Age":3}"""))));

        var pet = await request.ReadAsJsonAsync(TestJsonContext.Default.Pet);

        Assert.NotNull(pet);
        Assert.Equal("neo", pet.Name);
        Assert.Equal(3, pet.Age);
    }

    [Fact]
    public void ReadsCfProperties()
    {
        var (interop, runtime) = FakeWorld.Create();
        using var request = HttpRequest.FromJsObject(runtime.WrapToJsObject(interop.Retain(
            FakeWorld.CreateRequest("GET", "https://example.com/", cf: new FakeObject { ["colo"] = "NRT" }))));

        Assert.Equal("NRT", request.GetCfPropertyAsString("colo"));
        Assert.Null(request.GetCfPropertyAsString("country"));
    }
}

public sealed record Pet(string Name, int Age);

[JsonSerializable(typeof(Pet))]
[JsonSerializable(typeof(Pet[]))]
public sealed partial class TestJsonContext : JsonSerializerContext;
