using Cloudflare.Workers.Hosting.Tests.Fakes;
using Xunit;

namespace Cloudflare.Workers.Hosting.Tests;

public class HeadersTests
{
    [Fact]
    public void Set_ReplacesExistingValuesCaseInsensitively()
    {
        var headers = new HttpHeaders();
        headers.Add("X-Custom", "a");
        headers.Add("x-custom", "b");
        headers.Replace("X-CUSTOM", "c");

        Assert.Equal("c", headers.Get("x-custom"));
        Assert.Equal(1, headers.Count);
    }

    [Fact]
    public void Get_JoinsMultipleValuesWithComma()
    {
        var headers = new HttpHeaders();
        headers.Add("Accept", "text/html");
        headers.Add("accept", "application/json");

        Assert.Equal("text/html, application/json", headers.Get("ACCEPT"));
    }

    [Fact]
    public void Get_ReturnsNullForMissingHeader()
    {
        Assert.Null(new HttpHeaders().Get("missing"));
    }

    [Fact]
    public void Delete_RemovesAllValues()
    {
        var headers = new HttpHeaders();
        headers.Add("a", "1");
        headers.Add("A", "2");
        headers.Delete("a");

        Assert.False(headers.Has("a"));
        Assert.Equal(0, headers.Count);
    }

    [Fact]
    public void FromJs_ReadsAllEntries()
    {
        var (interop, runtime) = FakeWorld.Create();
        using var jsHeaders = runtime.WrapToJsObject(interop.Retain(
            FakeWorld.CreateHeaders(("content-type", "text/plain"), ("x-a", "1"))));

        var headers = HttpHeaders.FromJsObject(jsHeaders);

        Assert.Equal("text/plain", headers.Get("Content-Type"));
        Assert.Equal("1", headers.Get("x-a"));
        Assert.Equal(2, headers.Count);
    }
}
