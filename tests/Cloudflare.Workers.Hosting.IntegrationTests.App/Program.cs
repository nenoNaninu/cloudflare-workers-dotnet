using System.Net.Http.Headers;
using System.Text.Json.Serialization;
using Cloudflare.Workers.Hosting;
using Cloudflare.Workers.Hosting.Interop;

// Worker exercised by tests/Cloudflare.Workers.Hosting.IntegrationTests through `wrangler dev`.
// Every route targets one part of the library that only a real Workers runtime can verify.

var builder = WorkerApplication.CreateBuilder();

// -- basics / strings / bytes ------------------------------------------------------------

builder.MapGet("/ping", static _ => Task.FromResult(HttpResponse.Text("pong")));

builder.MapPost("/echo/text", static async ctx => HttpResponse.Text(await ctx.Request.ReadAsStringAsync()));

builder.MapPost("/echo/bytes", static async ctx => HttpResponse.Binary(await ctx.Request.ReadAsBytesAsync()));

builder.MapPost("/echo/json", static async ctx =>
{
    var person = await ctx.Request.ReadAsJsonAsync(IntegrationTestJsonContext.Default.Person);
    return HttpResponse.Json(person! with { Age = person.Age + 1 }, IntegrationTestJsonContext.Default.Person);
});

builder.MapGet("/bytes/:n", static ctx =>
{
    int n = int.Parse(ctx.Parameters["n"]);
    var bytes = new byte[n];
    for (int i = 0; i < n; i++)
    {
        bytes[i] = (byte)i;
    }

    return Task.FromResult(HttpResponse.Binary(bytes));
});

builder.MapGet("/unicode", static _ => Task.FromResult(HttpResponse.Text("こんにちは 🌏 café")));

// -- request / response headers ----------------------------------------------------------

builder.MapGet("/headers", static ctx =>
{
    var pairs = ctx.Request.Headers.Select(h => new HeaderPair(h.Key, h.Value)).ToArray();
    return Task.FromResult(HttpResponse.Json(pairs, IntegrationTestJsonContext.Default.HeaderPairArray));
});

builder.MapGet("/response-headers", static _ =>
{
    var response = HttpResponse.Text("headers", 218);
    response.Headers.Add("x-first", "one");
    response.Headers.Add("x-second", "two");
    response.Headers.Add("x-multi", "a");
    response.Headers.Add("x-multi", "b");
    return Task.FromResult(response);
});

builder.MapGet("/redirect", static _ => Task.FromResult(HttpResponse.Redirect("/ping", 302)));

// -- env / errors ------------------------------------------------------------------------

builder.MapGet("/env", static ctx => Task.FromResult(HttpResponse.Json(
    new EnvInfo(
        ctx.Env.Var("INTEGRATION_TEST_VAR"),
        ctx.Env.Var("INTEGRATION_TEST_MISSING"),
        ctx.Env.HasBinding("INTEGRATION_TEST_KV"),
        ctx.Env.HasBinding("INTEGRATION_TEST_NOPE")),
    IntegrationTestJsonContext.Default.EnvInfo)));

builder.MapGet("/env/missing-kv", static ctx =>
{
    try
    {
        using var kv = ctx.Env.Kv("INTEGRATION_TEST_NOPE");
        return Task.FromResult(HttpResponse.Text("no error", 500));
    }
    catch (InvalidOperationException ex)
    {
        return Task.FromResult(HttpResponse.Text(ex.Message, 404));
    }
});

builder.MapGet("/throw", static _ => throw new InvalidOperationException("boom from handler"));

builder.MapGet("/throw-async", static async _ =>
{
    await WorkerTimer.Delay(10);
    throw new InvalidOperationException("boom after await");
});

// -- promises / timers / waitUntil ---------------------------------------------------------

builder.MapGet("/delay/:ms", static async ctx =>
{
    double ms = double.Parse(ctx.Parameters["ms"]);
    await WorkerTimer.Delay(ms);
    return HttpResponse.Text($"slept {ms}");
});

// Five 300ms timers awaited together must overlap (well below 5 * 300 ms).
builder.MapGet("/parallel-delay", static async _ =>
{
    var start = DateTime.UtcNow;
    await Task.WhenAll(Enumerable.Range(0, 5).Select(static _ => WorkerTimer.Delay(300)));
    var elapsed = (DateTime.UtcNow - start).TotalMilliseconds;
    return HttpResponse.Text(((int)elapsed).ToString());
});

builder.MapGet("/wait-until/:key", static ctx =>
{
    string key = ctx.Parameters["key"];
    var kv = ctx.Env.Kv("INTEGRATION_TEST_KV");
    // No Task.Run: the wasm runtime is single-threaded and has no thread pool to run it on.
    ctx.WorkerContext.WaitUntil(PutAfterReleaseAsync(kv, key, Upstream(ctx)));

    return Task.FromResult(HttpResponse.Text("accepted", 202));
});

builder.OnScheduled(static async (evt, env, _) =>
{
    using var kv = env.Kv("INTEGRATION_TEST_KV");
    await kv.PutAsync("scheduled:last", $"{evt.Cron}|{evt.ScheduledTime.ToUnixTimeMilliseconds() > 0}");
});

// -- KV ----------------------------------------------------------------------------------

builder.MapPut("/kv/:key", static async ctx =>
{
    using var kv = ctx.Env.Kv("INTEGRATION_TEST_KV");
    string? ttl = Query(ctx, "ttl");
    var options = ttl is null ? null : new KvPutOptions { ExpirationTtl = TimeSpan.FromSeconds(int.Parse(ttl)) };
    await kv.PutAsync(ctx.Parameters["key"], await ctx.Request.ReadAsStringAsync(), options);
    return HttpResponse.Empty();
});

builder.MapGet("/kv/:key", static async ctx =>
{
    using var kv = ctx.Env.Kv("INTEGRATION_TEST_KV");
    string? value = await kv.GetTextAsync(ctx.Parameters["key"]);
    return value is null ? HttpResponse.NotFound() : HttpResponse.Text(value);
});

builder.MapDelete("/kv/:key", static async ctx =>
{
    using var kv = ctx.Env.Kv("INTEGRATION_TEST_KV");
    await kv.DeleteAsync(ctx.Parameters["key"]);
    return HttpResponse.NoContent();
});

builder.MapPut("/kv-bytes/:key", static async ctx =>
{
    using var kv = ctx.Env.Kv("INTEGRATION_TEST_KV");
    await kv.PutAsync(ctx.Parameters["key"], await ctx.Request.ReadAsBytesAsync());
    return HttpResponse.Empty();
});

builder.MapGet("/kv-bytes/:key", static async ctx =>
{
    using var kv = ctx.Env.Kv("INTEGRATION_TEST_KV");
    byte[]? value = await kv.GetBytesAsync(ctx.Parameters["key"]);
    return value is null ? HttpResponse.NotFound() : HttpResponse.Binary(value);
});

builder.MapGet("/kv-list", static async ctx =>
{
    using var kv = ctx.Env.Kv("INTEGRATION_TEST_KV");
    string? limit = Query(ctx, "limit");
    var result = await kv.ListAsync(new KvListOptions
    {
        Prefix = Query(ctx, "prefix"),
        Limit = limit is null ? null : int.Parse(limit),
        Cursor = Query(ctx, "cursor"),
    });

    return HttpResponse.Json(
        new KvListInfo(result.Keys.Select(k => k.Name).ToArray(), result.ListComplete, result.Cursor,
            result.Keys.Any(k => k.Expiration is not null)),
        IntegrationTestJsonContext.Default.KvListInfo);
});

// -- R2 ----------------------------------------------------------------------------------

builder.MapPut("/r2/:key", static async ctx =>
{
    using var r2 = ctx.Env.R2("INTEGRATION_TEST_R2");
    using var put = await r2.PutAsync(ctx.Parameters["key"], await ctx.Request.ReadAsBytesAsync());
    return HttpResponse.Json(new R2Info(put.Key, put.Size, put.Etag), IntegrationTestJsonContext.Default.R2Info);
});

builder.MapGet("/r2/:key", static async ctx =>
{
    using var r2 = ctx.Env.R2("INTEGRATION_TEST_R2");
    using var obj = await r2.GetAsync(ctx.Parameters["key"]);
    return obj is null ? HttpResponse.NotFound() : HttpResponse.Binary(await obj.BodyBytesAsync());
});

builder.MapGet("/r2/:key/text", static async ctx =>
{
    using var r2 = ctx.Env.R2("INTEGRATION_TEST_R2");
    using var obj = await r2.GetAsync(ctx.Parameters["key"]);
    return obj is null ? HttpResponse.NotFound() : HttpResponse.Text(await obj.BodyTextAsync());
});

builder.MapGet("/r2/:key/head", static async ctx =>
{
    using var r2 = ctx.Env.R2("INTEGRATION_TEST_R2");
    using var obj = await r2.HeadAsync(ctx.Parameters["key"]);
    return obj is null
        ? HttpResponse.NotFound()
        : HttpResponse.Json(new R2Info(obj.Key, obj.Size, obj.Etag), IntegrationTestJsonContext.Default.R2Info);
});

builder.MapDelete("/r2/:key", static async ctx =>
{
    using var r2 = ctx.Env.R2("INTEGRATION_TEST_R2");
    await r2.DeleteAsync(ctx.Parameters["key"]);
    return HttpResponse.NoContent();
});

// -- D1 ----------------------------------------------------------------------------------

builder.MapPost("/d1/reset", static async ctx =>
{
    using var db = ctx.Env.D1("INTEGRATION_TEST_DB");
    using (var create = db.Prepare(
        "CREATE TABLE IF NOT EXISTS items (id INTEGER PRIMARY KEY AUTOINCREMENT, name TEXT NOT NULL, price REAL, note TEXT)"))
    {
        await create.RunAsync();
    }

    using var delete = db.Prepare("DELETE FROM items");
    await delete.RunAsync();
    return HttpResponse.Empty();
});

builder.MapPost("/d1/items", static async ctx =>
{
    var item = (await ctx.Request.ReadAsJsonAsync(IntegrationTestJsonContext.Default.Item))!;
    using var db = ctx.Env.D1("INTEGRATION_TEST_DB");
    using var insert = db.Prepare("INSERT INTO items (name, price, note) VALUES (?1, ?2, ?3)")
        .Bind(JsArg.From(item.Name), JsArg.From(item.Price), item.Note is null ? JsArg.Null : JsArg.From(item.Note));
    var result = await insert.RunAsync();
    return HttpResponse.Json(
        new D1RunInfo(result.IsSuccess, result.Changes, result.LastRowId),
        IntegrationTestJsonContext.Default.D1RunInfo);
});

builder.MapGet("/d1/items", static async ctx =>
{
    using var db = ctx.Env.D1("INTEGRATION_TEST_DB");
    using var select = db.Prepare("SELECT id, name, price, note FROM items ORDER BY id");
    return HttpResponse.Json(await select.AllJsonAsync());
});

builder.MapGet("/d1/items/typed", static async ctx =>
{
    using var db = ctx.Env.D1("INTEGRATION_TEST_DB");
    using var select = db.Prepare("SELECT id, name, price, note FROM items ORDER BY id");
    var items = await select.AllAsync(IntegrationTestJsonContext.Default.ItemRowArray) ?? [];
    return HttpResponse.Json(items, IntegrationTestJsonContext.Default.ItemRowArray);
});

builder.MapGet("/d1/items/:id", static async ctx =>
{
    using var db = ctx.Env.D1("INTEGRATION_TEST_DB");
    using var select = db.Prepare("SELECT id, name, price, note FROM items WHERE id = ?1")
        .Bind(JsArg.From(int.Parse(ctx.Parameters["id"])));
    string? json = await select.FirstJsonAsync();
    return json is null ? HttpResponse.NotFound() : HttpResponse.Json(json);
});

builder.MapGet("/d1/invalid", static async ctx =>
{
    using var db = ctx.Env.D1("INTEGRATION_TEST_DB");
    using var bad = db.Prepare("SELECT * FROM no_such_table");
    try
    {
        await bad.AllJsonAsync();
        return HttpResponse.Text("no error", 500);
    }
    catch (JsException ex)
    {
        return HttpResponse.Text(ex.Message, 400);
    }
});

// -- Service binding ---------------------------------------------------------------------

builder.MapPost("/service/echo", static async ctx =>
{
    using var upstream = ctx.Env.Service("INTEGRATION_TEST_UPSTREAM");
    using var response = await upstream.FetchAsync(new FetchRequestMessage("http://upstream/echo")
    {
        Method = "POST",
        Body = await ctx.Request.ReadAsStringAsync(),
    });

    return HttpResponse.Text(await response.ReadAsStringAsync(), response.StatusCode);
});

// -- Fetch -------------------------------------------------------------------------------

builder.MapPost("/fetch/echo", static async ctx =>
{
    var request = new FetchRequestMessage(Upstream(ctx) + "/echo?from=fetch")
    {
        Method = "POST",
        Body = await ctx.Request.ReadAsStringAsync(),
    };
    request.Headers.Add("x-integration-test", "fetch");
    request.Headers.Add("content-type", "text/plain");

    using var response = await Fetch.FetchAsync(request);
    return HttpResponse.Text(await response.ReadAsStringAsync(), response.StatusCode);
});

builder.MapGet("/fetch/headers", static async ctx =>
{
    using var response = await Fetch.FetchAsync(Upstream(ctx) + "/headers");
    var headers = response.ReadHeaders();
    return HttpResponse.Json(
        new FetchHeadersInfo(response.StatusCode, headers.Get("x-upstream"), headers.Get("x-multi"), headers.Get("content-type")),
        IntegrationTestJsonContext.Default.FetchHeadersInfo);
});

builder.MapGet("/fetch/redirect/:mode", static async ctx =>
{
    var request = new FetchRequestMessage(Upstream(ctx) + "/redirect")
    {
        Redirect = ctx.Parameters["mode"] switch
        {
            "manual" => FetchRedirectMode.Manual,
            "error" => FetchRedirectMode.Error,
            _ => FetchRedirectMode.Follow,
        },
    };

    try
    {
        using var response = await Fetch.FetchAsync(request);
        return HttpResponse.Text($"{response.StatusCode}|{response.ReadHeaders().Get("location")}");
    }
    catch (JsException ex)
    {
        return HttpResponse.Text(ex.Message, 502);
    }
});

builder.MapGet("/fetch/bytes", static async ctx =>
{
    using var response = await Fetch.FetchAsync(Upstream(ctx) + "/bytes");
    return HttpResponse.Binary(await response.ReadAsBytesAsync());
});

builder.MapGet("/fetch/json", static async ctx =>
{
    using var response = await Fetch.FetchAsync(Upstream(ctx) + "/json");
    var person = await response.ReadAsJsonAsync(IntegrationTestJsonContext.Default.Person);
    return HttpResponse.Json(person!, IntegrationTestJsonContext.Default.Person);
});

builder.MapGet("/fetch/unreachable", static async _ =>
{
    try
    {
        using var response = await Fetch.FetchAsync("http://127.0.0.1:1/");
        return HttpResponse.Text($"unexpected {response.StatusCode}", 500);
    }
    catch (JsException ex)
    {
        return HttpResponse.Text(ex.Message, 502);
    }
});

// -- HttpClient / FetchHttpMessageHandler ----------------------------------------------------

builder.MapPost("/http-client/echo", static async ctx =>
{
    using var client = CreateHttpClient(ctx);
    using var request = new HttpRequestMessage(HttpMethod.Post, "echo?from=http-client")
    {
        Content = new ByteArrayContent(await ctx.Request.ReadAsBytesAsync()),
    };
    request.Headers.Add("x-integration-test", "http-client");
    request.Headers.Add("x-multi", ["a", "b"]);
    request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(
        ctx.Request.Headers.Get("content-type") ?? "application/octet-stream");
    request.Content.Headers.Add("x-content", ["one", "two"]);
    // A stale length must not be forwarded: fetch computes the actual length from the body.
    request.Content.Headers.ContentLength = 999;

    using var response = await client.SendAsync(request);
    return await ForwardHttpResponseAsync(response);
});

builder.MapGet("/http-client/empty/:method", static async ctx =>
{
    using var client = CreateHttpClient(ctx);
    using var request = new HttpRequestMessage(new HttpMethod(ctx.Parameters["method"]), "echo")
    {
        Content = new ByteArrayContent([]),
    };

    using var response = await client.SendAsync(request);
    return await DescribeHttpResponseAsync(response, request);
});

builder.MapGet("/http-client/response/:kind", static async ctx =>
{
    using var client = CreateHttpClient(ctx);
    using var request = new HttpRequestMessage(HttpMethod.Get, ctx.Parameters["kind"]);
    using var response = await client.SendAsync(request);
    return Query(ctx, "metadata") == "true"
        ? await DescribeHttpResponseAsync(response, request)
        : await ForwardHttpResponseAsync(response);
});

builder.MapGet("/http-client/redirect/:mode", static async ctx =>
{
    using var client = CreateHttpClient(ctx, allowAutoRedirect: ctx.Parameters["mode"] != "manual");
    using var response = await client.GetAsync("redirect");
    return await ForwardHttpResponseAsync(response);
});

builder.MapGet("/http-client/unreachable", static async _ =>
{
    using var client = new HttpClient(new FetchHttpMessageHandler())
    {
        Timeout = Timeout.InfiniteTimeSpan,
    };
    try
    {
        using var response = await client.GetAsync("http://127.0.0.1:1/");
        return HttpResponse.Text($"unexpected {response.StatusCode}", 500);
    }
    catch (JsException ex)
    {
        return HttpResponse.Text(ex.Message, 502);
    }
});

// -- handle lifetime -----------------------------------------------------------------------

// Creates and releases many JS handles in a single request to shake out leaks / double frees.
builder.MapGet("/stress/handles/:n", static async ctx =>
{
    int n = int.Parse(ctx.Parameters["n"]);
    using var kv = ctx.Env.Kv("INTEGRATION_TEST_KV");
    for (int i = 0; i < n; i++)
    {
        await kv.PutAsync("stress:" + (i % 10), "value-" + i);
        _ = await kv.GetTextAsync("stress:" + (i % 10));
    }

    return HttpResponse.Text(n.ToString());
});

builder.Build().Run();

static HttpClient CreateHttpClient(HttpContext ctx, bool allowAutoRedirect = true)
    => new(new FetchHttpMessageHandler { AllowAutoRedirect = allowAutoRedirect })
    {
        BaseAddress = new Uri(Upstream(ctx) + "/"),
        // HttpClient's timeout uses WASI timers that the shim does not implement.
        // The test fixture's client still bounds every request with a timeout.
        Timeout = Timeout.InfiniteTimeSpan,
    };

static async Task<HttpResponse> ForwardHttpResponseAsync(HttpResponseMessage response)
{
    var result = (int)response.StatusCode == 204
        ? HttpResponse.NoContent()
        : HttpResponse.Binary(await response.Content.ReadAsByteArrayAsync(), (int)response.StatusCode,
            response.Content.Headers.ContentType?.ToString() ?? "application/octet-stream");
    foreach (var header in response.Headers)
    {
        result.Headers.Add(header.Key, string.Join(", ", header.Value));
    }
    return result;
}

static async Task<HttpResponse> DescribeHttpResponseAsync(HttpResponseMessage response, HttpRequestMessage request)
{
    var body = await response.Content.ReadAsByteArrayAsync();
    return HttpResponse.Json(new HttpClientResponseInfo(
        (int)response.StatusCode,
        Convert.ToBase64String(body),
        response.Headers.TryGetValues("x-upstream", out var upstream) ? string.Join(", ", upstream) : null,
        response.Headers.TryGetValues("x-multi", out var multi) ? string.Join(", ", multi) : null,
        response.Content.Headers.ContentType?.ToString(),
        response.Content.Headers.ContentLength,
        response.Content.Headers.ContentEncoding.ToArray(),
        response.RequestMessage?.RequestUri?.AbsoluteUri,
        ReferenceEquals(request, response.RequestMessage)), IntegrationTestJsonContext.Default.HttpClientResponseInfo);
}

static async Task PutAfterReleaseAsync(KvNamespace kv, string key, string upstreamUrl)
{
    try
    {
        // The test releases this request only after receiving the Worker's response.
        using var response = await Fetch.FetchAsync(upstreamUrl + "/wait-until/" + key);
        if (response.StatusCode != 204)
        {
            throw new InvalidOperationException($"waitUntil gate returned {response.StatusCode}.");
        }
        await kv.PutAsync(key, "done-after-response");
    }
    finally
    {
        kv.Dispose();
    }
}


static string Upstream(HttpContext ctx)
    => ctx.Env.Var("UPSTREAM_URL") ?? throw new InvalidOperationException("UPSTREAM_URL is not configured.");

static string? Query(HttpContext ctx, string name)
{
    string query = ctx.Request.Uri.Query;
    if (query.Length == 0)
    {
        return null;
    }

    foreach (string part in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
    {
        int eq = part.IndexOf('=');
        string key = Uri.UnescapeDataString(eq < 0 ? part : part[..eq]);
        if (key == name)
        {
            return eq < 0 ? string.Empty : Uri.UnescapeDataString(part[(eq + 1)..]);
        }
    }

    return null;
}

public sealed record Person(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("age")] int Age);

public sealed record HeaderPair(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("value")] string Value);

public sealed record EnvInfo(
    [property: JsonPropertyName("var")] string? Var,
    [property: JsonPropertyName("missing")] string? Missing,
    [property: JsonPropertyName("hasKv")] bool HasKv,
    [property: JsonPropertyName("hasNope")] bool HasNope);

public sealed record KvListInfo(
    [property: JsonPropertyName("keys")] string[] Keys,
    [property: JsonPropertyName("listComplete")] bool ListComplete,
    [property: JsonPropertyName("cursor")] string? Cursor,
    [property: JsonPropertyName("hasExpiration")] bool HasExpiration);

public sealed record R2Info(
    [property: JsonPropertyName("key")] string Key,
    [property: JsonPropertyName("size")] long Size,
    [property: JsonPropertyName("etag")] string? Etag);

public sealed record Item(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("price")] double Price,
    [property: JsonPropertyName("note")] string? Note);

public sealed record ItemRow(
    [property: JsonPropertyName("id")] long Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("price")] double Price,
    [property: JsonPropertyName("note")] string? Note);

public sealed record D1RunInfo(
    [property: JsonPropertyName("success")] bool Success,
    [property: JsonPropertyName("changes")] long Changes,
    [property: JsonPropertyName("lastRowId")] long? LastRowId);

public sealed record FetchHeadersInfo(
    [property: JsonPropertyName("status")] int Status,
    [property: JsonPropertyName("upstream")] string? Upstream,
    [property: JsonPropertyName("multi")] string? Multi,
    [property: JsonPropertyName("contentType")] string? ContentType);

public sealed record HttpClientResponseInfo(
    [property: JsonPropertyName("status")] int Status,
    [property: JsonPropertyName("bodyBase64")] string BodyBase64,
    [property: JsonPropertyName("upstream")] string? Upstream,
    [property: JsonPropertyName("multi")] string? Multi,
    [property: JsonPropertyName("contentType")] string? ContentType,
    [property: JsonPropertyName("contentLength")] long? ContentLength,
    [property: JsonPropertyName("contentEncoding")] string[] ContentEncoding,
    [property: JsonPropertyName("requestUrl")] string? RequestUrl,
    [property: JsonPropertyName("sameRequest")] bool SameRequest);

[JsonSerializable(typeof(Person))]
[JsonSerializable(typeof(HeaderPair[]))]
[JsonSerializable(typeof(EnvInfo))]
[JsonSerializable(typeof(KvListInfo))]
[JsonSerializable(typeof(R2Info))]
[JsonSerializable(typeof(Item))]
[JsonSerializable(typeof(ItemRow[]))]
[JsonSerializable(typeof(D1RunInfo))]
[JsonSerializable(typeof(FetchHeadersInfo))]
[JsonSerializable(typeof(HttpClientResponseInfo))]
public sealed partial class IntegrationTestJsonContext : JsonSerializerContext;
