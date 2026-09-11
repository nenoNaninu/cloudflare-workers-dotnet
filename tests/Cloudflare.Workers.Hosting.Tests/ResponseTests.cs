using Cloudflare.Workers.Hosting.Tests.Fakes;
using Xunit;

namespace Cloudflare.Workers.Hosting.Tests;

public class ResponseTests
{
    [Fact]
    public void Text_MaterializesJsResponseWithBodyStatusAndContentType()
    {
        var (interop, runtime) = FakeWorld.Create();
        var response = HttpResponse.Text("hi", status: 201);

        using var js = response.ToJsObject(runtime);
        var fake = Assert.IsType<FakeObject>(interop.Get(js.Handle));

        Assert.Equal("hi", fake["body"]);
        Assert.Equal(201d, fake["status"]);
        var headerPair = Assert.IsType<FakeArray>(Assert.IsType<FakeArray>(fake["headers"])[0]);
        Assert.Equal("content-type", headerPair[0]);
        Assert.Equal("text/plain; charset=utf-8", headerPair[1]);
    }

    [Fact]
    public void Json_SerializesValueAndSetsContentType()
    {
        var (interop, runtime) = FakeWorld.Create();
        var response = HttpResponse.Json(new Pet("rex", 5), TestJsonContext.Default.Pet);

        using var js = response.ToJsObject(runtime);
        var fake = Assert.IsType<FakeObject>(interop.Get(js.Handle));

        Assert.Equal("""{"Name":"rex","Age":5}"""u8.ToArray(), Assert.IsType<byte[]>(fake["body"]));
        Assert.Equal("application/json; charset=utf-8", response.Headers.Get("content-type"));
    }

    [Fact]
    public void Bytes_PassesBinaryBody()
    {
        var (interop, runtime) = FakeWorld.Create();
        byte[] payload = [9, 8, 7];
        var response = HttpResponse.Binary(payload, contentType: "image/png");

        using var js = response.ToJsObject(runtime);
        var fake = Assert.IsType<FakeObject>(interop.Get(js.Handle));

        Assert.Equal(payload, fake["body"]);
        Assert.Equal("image/png", response.Headers.Get("content-type"));
    }

    [Fact]
    public void Redirect_SetsLocationHeaderAndStatus()
    {
        var response = HttpResponse.Redirect("https://example.com/next");

        Assert.Equal(302, response.StatusCode);
        Assert.Equal("https://example.com/next", response.Headers.Get("location"));
    }

    [Fact]
    public void Empty_HasNullBody()
    {
        var (interop, runtime) = FakeWorld.Create();
        var response = HttpResponse.Empty();

        using var js = response.ToJsObject(runtime);
        var fake = Assert.IsType<FakeObject>(interop.Get(js.Handle));

        Assert.Null(fake["body"]);
        Assert.Equal(200d, fake["status"]);
    }

    [Fact]
    public void JsonString_MaterializesTextBody()
    {
        var (interop, runtime) = FakeWorld.Create();
        var response = HttpResponse.Json("{\"ok\":true}", status: 202);

        using var js = response.ToJsObject(runtime);
        var fake = Assert.IsType<FakeObject>(interop.Get(js.Handle));

        Assert.Equal("{\"ok\":true}", fake["body"]);
        Assert.Equal(202d, fake["status"]);
        Assert.Equal("application/json; charset=utf-8", response.Headers.Get("content-type"));
    }

    [Fact]
    public void Error_DefaultsTo500()
    {
        var response = HttpResponse.Error("failed");

        Assert.Equal(500, response.StatusCode);
    }

    [Fact]
    public void NotFound_DefaultsTo404()
    {
        var response = HttpResponse.NotFound();

        Assert.Equal(404, response.StatusCode);
    }
}
