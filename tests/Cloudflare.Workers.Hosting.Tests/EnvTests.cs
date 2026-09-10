using Cloudflare.Workers.Hosting.Tests.Fakes;
using Xunit;

namespace Cloudflare.Workers.Hosting.Tests;

public class EnvTests
{
    [Fact]
    public void Var_ReadsStringBinding()
    {
        var (interop, runtime) = FakeWorld.Create();
        using var env = FakeWorld.CreateEnv(runtime, interop, new FakeObject { ["API_HOST"] = "api.example.com" });

        Assert.Equal("api.example.com", env.Var("API_HOST"));
        Assert.Null(env.Var("MISSING"));
    }

    [Fact]
    public void HasBinding_DetectsPresence()
    {
        var (interop, runtime) = FakeWorld.Create();
        using var env = FakeWorld.CreateEnv(runtime, interop, new FakeObject { ["KV"] = new FakeObject() });

        Assert.True(env.HasBinding("KV"));
        Assert.False(env.HasBinding("R2"));
    }

    [Fact]
    public void Kv_ThrowsForMissingBinding()
    {
        var (interop, runtime) = FakeWorld.Create();
        using var env = FakeWorld.CreateEnv(runtime, interop);

        var exception = Assert.Throws<InvalidOperationException>(() => env.Kv("NOPE"));
        Assert.Contains("NOPE", exception.Message);
    }
}
