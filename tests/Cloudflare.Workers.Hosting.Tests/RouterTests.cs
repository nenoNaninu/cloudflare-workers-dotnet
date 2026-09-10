using Cloudflare.Workers.Hosting.Tests.Fakes;
using Xunit;

namespace Cloudflare.Workers.Hosting.Tests;

public class RouterTests
{
    [Fact]
    public async Task MatchesExactPath()
    {
        var router = new HttpRequestRouter()
            .Get("/", static _ => Task.FromResult(HttpResponse.Text("root")))
            .Get("/about", static _ => Task.FromResult(HttpResponse.Text("about")));

        Assert.Equal("root", await HandleAndReadBody(router, "GET", "https://x.dev/"));
        Assert.Equal("about", await HandleAndReadBody(router, "GET", "https://x.dev/about"));
    }

    [Fact]
    public async Task CapturesNamedParameters()
    {
        var router = new HttpRequestRouter()
            .Get("/users/:id/posts/:postId", static ctx =>
                Task.FromResult(HttpResponse.Text($"{ctx.Parameters["id"]}:{ctx.Parameters["postId"]}")));

        Assert.Equal("42:7", await HandleAndReadBody(router, "GET", "https://x.dev/users/42/posts/7"));
    }

    [Fact]
    public async Task UnescapesParameters()
    {
        var router = new HttpRequestRouter()
            .Get("/kv/:key", static ctx => Task.FromResult(HttpResponse.Text(ctx.Parameters["key"])));

        Assert.Equal("hello world", await HandleAndReadBody(router, "GET", "https://x.dev/kv/hello%20world"));
    }

    [Fact]
    public async Task WildcardCapturesRemainingSegments()
    {
        var router = new HttpRequestRouter()
            .Get("/static/*path", static ctx => Task.FromResult(HttpResponse.Text(ctx.Parameters["path"])));

        Assert.Equal("css/site.css", await HandleAndReadBody(router, "GET", "https://x.dev/static/css/site.css"));
    }

    [Fact]
    public async Task FiltersOnHttpMethod()
    {
        var router = new HttpRequestRouter()
            .Get("/thing", static _ => Task.FromResult(HttpResponse.Text("got")))
            .Post("/thing", static _ => Task.FromResult(HttpResponse.Text("posted")));

        Assert.Equal("got", await HandleAndReadBody(router, "GET", "https://x.dev/thing"));
        Assert.Equal("posted", await HandleAndReadBody(router, "POST", "https://x.dev/thing"));
    }

    [Fact]
    public async Task AllMatchesAnyMethod()
    {
        var router = new HttpRequestRouter()
            .All("/any", static context => Task.FromResult(HttpResponse.Text(context.Request.Method)));

        Assert.Equal("DELETE", await HandleAndReadBody(router, "DELETE", "https://x.dev/any"));
    }

    [Fact]
    public async Task UnmatchedPathReturns404()
    {
        var router = new HttpRequestRouter();
        var response = await Handle(router, "GET", "https://x.dev/missing");

        Assert.Equal(404, response.StatusCode);
    }

    [Fact]
    public async Task PartialPrefixDoesNotMatch()
    {
        var router = new HttpRequestRouter()
            .Get("/a", static _ => Task.FromResult(HttpResponse.Text("a")));

        Assert.Equal(404, (await Handle(router, "GET", "https://x.dev/a/b")).StatusCode);
        Assert.Equal(404, (await Handle(router, "GET", "https://x.dev/")).StatusCode);
    }

    [Fact]
    public async Task FallbackHandlesUnmatchedRoutes()
    {
        var router = new HttpRequestRouter()
            .Fallback(static context => Task.FromResult(HttpResponse.Text($"fallback:{context.Request.Path}", 404)));

        Assert.Equal("fallback:/nope", await HandleAndReadBody(router, "GET", "https://x.dev/nope"));
    }

    private static async Task<HttpResponse> Handle(HttpRequestRouter router, string method, string url)
    {
        var (interop, runtime) = FakeWorld.Create();
        using var request = HttpRequest.FromJsObject(runtime.WrapToJsObject(interop.Retain(FakeWorld.CreateRequest(method, url))));
        using var env = FakeWorld.CreateEnv(runtime, interop);
        using var context = FakeWorld.CreateContext(runtime, interop);
        return await router.HandleAsync(request, env, context);
    }

    private static async Task<string> HandleAndReadBody(HttpRequestRouter router, string method, string url)
    {
        var (interop, runtime) = FakeWorld.Create();
        using var request = HttpRequest.FromJsObject(runtime.WrapToJsObject(interop.Retain(FakeWorld.CreateRequest(method, url))));
        using var env = FakeWorld.CreateEnv(runtime, interop);
        using var context = FakeWorld.CreateContext(runtime, interop);
        var response = await router.HandleAsync(request, env, context);

        using var js = response.ToJsObject(runtime);
        var fake = Assert.IsType<FakeObject>(interop.Get(js.Handle));
        return Assert.IsType<string>(fake["body"]);
    }
}
