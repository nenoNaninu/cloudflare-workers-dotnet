using System.Text.Json.Serialization;
using Cloudflare.Workers.Hosting;
using Cloudflare.Workers.Hosting.Interop;

// Worker exercised by tests/Cloudflare.Workers.Hosting.IntegrationTests through `wrangler dev`.
// Every route targets one part of the library that only a real Workers runtime can verify.

var builder = WorkerApplication.CreateBuilder();

// -- basics / strings / bytes ------------------------------------------------------------

builder.MapGet("/ping", static _ => Task.FromResult(HttpResponse.Text("pong")));

builder.MapPost("/echo/text", static async c => HttpResponse.Text(await c.Request.ReadAsStringAsync()));

builder.MapPost("/echo/bytes", static async c => HttpResponse.Binary(await c.Request.ReadAsBytesAsync()));

builder.MapPost("/echo/json", static async c =>
{
    var person = await c.Request.ReadAsJsonAsync(E2EJsonContext.Default.Person);
    return HttpResponse.Json(person! with { Age = person.Age + 1 }, E2EJsonContext.Default.Person);
});

builder.MapGet("/bytes/:n", static c =>
{
    int n = int.Parse(c.Parameters["n"]);
    var bytes = new byte[n];
    for (int i = 0; i < n; i++)
    {
        bytes[i] = (byte)i;
    }

    return Task.FromResult(HttpResponse.Binary(bytes));
});

builder.MapGet("/unicode", static _ => Task.FromResult(HttpResponse.Text("こんにちは 🌏 café")));

// -- request / response headers ----------------------------------------------------------

builder.MapGet("/headers", static c =>
{
    var pairs = c.Request.Headers.Select(h => new HeaderPair(h.Key, h.Value)).ToArray();
    return Task.FromResult(HttpResponse.Json(pairs, E2EJsonContext.Default.HeaderPairArray));
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

builder.MapGet("/env", static c => Task.FromResult(HttpResponse.Json(
    new EnvInfo(
        c.Env.Var("E2E_VAR"),
        c.Env.Var("E2E_MISSING"),
        c.Env.HasBinding("E2E_KV"),
        c.Env.HasBinding("E2E_NOPE")),
    E2EJsonContext.Default.EnvInfo)));

builder.MapGet("/env/missing-kv", static c =>
{
    try
    {
        using var kv = c.Env.Kv("E2E_NOPE");
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

builder.MapGet("/delay/:ms", static async c =>
{
    double ms = double.Parse(c.Parameters["ms"]);
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

builder.MapGet("/wait-until/:key", static c =>
{
    string key = c.Parameters["key"];
    var kv = c.Env.Kv("E2E_KV");
    // No Task.Run: the wasm runtime is single-threaded and has no thread pool to run it on.
    c.WorkerContext.WaitUntil(PutAfterDelayAsync(kv, key));

    return Task.FromResult(HttpResponse.Text("accepted", 202));
});

builder.OnScheduled(static async (evt, env, _) =>
{
    using var kv = env.Kv("E2E_KV");
    await kv.PutAsync("scheduled:last", $"{evt.Cron}|{evt.ScheduledTime.ToUnixTimeMilliseconds() > 0}");
});

// -- KV ----------------------------------------------------------------------------------

builder.MapPut("/kv/:key", static async c =>
{
    using var kv = c.Env.Kv("E2E_KV");
    string? ttl = Query(c, "ttl");
    var options = ttl is null ? null : new KvPutOptions { ExpirationTtl = TimeSpan.FromSeconds(int.Parse(ttl)) };
    await kv.PutAsync(c.Parameters["key"], await c.Request.ReadAsStringAsync(), options);
    return HttpResponse.Empty();
});

builder.MapGet("/kv/:key", static async c =>
{
    using var kv = c.Env.Kv("E2E_KV");
    string? value = await kv.GetTextAsync(c.Parameters["key"]);
    return value is null ? HttpResponse.NotFound() : HttpResponse.Text(value);
});

builder.MapDelete("/kv/:key", static async c =>
{
    using var kv = c.Env.Kv("E2E_KV");
    await kv.DeleteAsync(c.Parameters["key"]);
    return HttpResponse.NoContent();
});

builder.MapPut("/kv-bytes/:key", static async c =>
{
    using var kv = c.Env.Kv("E2E_KV");
    await kv.PutAsync(c.Parameters["key"], await c.Request.ReadAsBytesAsync());
    return HttpResponse.Empty();
});

builder.MapGet("/kv-bytes/:key", static async c =>
{
    using var kv = c.Env.Kv("E2E_KV");
    byte[]? value = await kv.GetBytesAsync(c.Parameters["key"]);
    return value is null ? HttpResponse.NotFound() : HttpResponse.Binary(value);
});

builder.MapGet("/kv-list", static async c =>
{
    using var kv = c.Env.Kv("E2E_KV");
    string? limit = Query(c, "limit");
    var result = await kv.ListAsync(new KvListOptions
    {
        Prefix = Query(c, "prefix"),
        Limit = limit is null ? null : int.Parse(limit),
        Cursor = Query(c, "cursor"),
    });

    return HttpResponse.Json(
        new KvListInfo(result.Keys.Select(k => k.Name).ToArray(), result.ListComplete, result.Cursor,
            result.Keys.Any(k => k.Expiration is not null)),
        E2EJsonContext.Default.KvListInfo);
});

// -- R2 ----------------------------------------------------------------------------------

builder.MapPut("/r2/:key", static async c =>
{
    using var r2 = c.Env.R2("E2E_R2");
    using var put = await r2.PutAsync(c.Parameters["key"], await c.Request.ReadAsBytesAsync());
    return HttpResponse.Json(new R2Info(put.Key, put.Size, put.Etag), E2EJsonContext.Default.R2Info);
});

builder.MapGet("/r2/:key", static async c =>
{
    using var r2 = c.Env.R2("E2E_R2");
    using var obj = await r2.GetAsync(c.Parameters["key"]);
    return obj is null ? HttpResponse.NotFound() : HttpResponse.Binary(await obj.BodyBytesAsync());
});

builder.MapGet("/r2/:key/text", static async c =>
{
    using var r2 = c.Env.R2("E2E_R2");
    using var obj = await r2.GetAsync(c.Parameters["key"]);
    return obj is null ? HttpResponse.NotFound() : HttpResponse.Text(await obj.BodyTextAsync());
});

builder.MapGet("/r2/:key/head", static async c =>
{
    using var r2 = c.Env.R2("E2E_R2");
    using var obj = await r2.HeadAsync(c.Parameters["key"]);
    return obj is null
        ? HttpResponse.NotFound()
        : HttpResponse.Json(new R2Info(obj.Key, obj.Size, obj.Etag), E2EJsonContext.Default.R2Info);
});

builder.MapDelete("/r2/:key", static async c =>
{
    using var r2 = c.Env.R2("E2E_R2");
    await r2.DeleteAsync(c.Parameters["key"]);
    return HttpResponse.NoContent();
});

// -- D1 ----------------------------------------------------------------------------------

builder.MapPost("/d1/reset", static async c =>
{
    using var db = c.Env.D1("E2E_DB");
    using (var create = db.Prepare(
        "CREATE TABLE IF NOT EXISTS items (id INTEGER PRIMARY KEY AUTOINCREMENT, name TEXT NOT NULL, price REAL, note TEXT)"))
    {
        await create.RunAsync();
    }

    using var delete = db.Prepare("DELETE FROM items");
    await delete.RunAsync();
    return HttpResponse.Empty();
});

builder.MapPost("/d1/items", static async c =>
{
    var item = (await c.Request.ReadAsJsonAsync(E2EJsonContext.Default.Item))!;
    using var db = c.Env.D1("E2E_DB");
    using var insert = db.Prepare("INSERT INTO items (name, price, note) VALUES (?1, ?2, ?3)")
        .Bind(JsArg.From(item.Name), JsArg.From(item.Price), item.Note is null ? JsArg.Null : JsArg.From(item.Note));
    var result = await insert.RunAsync();
    return HttpResponse.Json(
        new D1RunInfo(result.IsSuccess, result.Changes, result.LastRowId),
        E2EJsonContext.Default.D1RunInfo);
});

builder.MapGet("/d1/items", static async c =>
{
    using var db = c.Env.D1("E2E_DB");
    using var select = db.Prepare("SELECT id, name, price, note FROM items ORDER BY id");
    return HttpResponse.Json(await select.AllJsonAsync());
});

builder.MapGet("/d1/items/typed", static async c =>
{
    using var db = c.Env.D1("E2E_DB");
    using var select = db.Prepare("SELECT id, name, price, note FROM items ORDER BY id");
    var items = await select.AllAsync(E2EJsonContext.Default.ItemRowArray) ?? [];
    return HttpResponse.Json(items, E2EJsonContext.Default.ItemRowArray);
});

builder.MapGet("/d1/items/:id", static async c =>
{
    using var db = c.Env.D1("E2E_DB");
    using var select = db.Prepare("SELECT id, name, price, note FROM items WHERE id = ?1")
        .Bind(JsArg.From(int.Parse(c.Parameters["id"])));
    string? json = await select.FirstJsonAsync();
    return json is null ? HttpResponse.NotFound() : HttpResponse.Json(json);
});

builder.MapGet("/d1/invalid", static async c =>
{
    using var db = c.Env.D1("E2E_DB");
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

builder.MapPost("/service/echo", static async c =>
{
    using var upstream = c.Env.Service("E2E_UPSTREAM");
    using var response = await upstream.FetchAsync(new FetchRequestMessage("http://upstream/echo")
    {
        Method = "POST",
        Body = await c.Request.ReadAsStringAsync(),
    });

    return HttpResponse.Text(await response.ReadAsStringAsync(), response.StatusCode);
});

// -- Fetch -------------------------------------------------------------------------------

builder.MapPost("/fetch/echo", static async c =>
{
    var request = new FetchRequestMessage(Upstream(c) + "/echo?from=fetch")
    {
        Method = "POST",
        Body = await c.Request.ReadAsStringAsync(),
    };
    request.Headers.Add("x-e2e", "fetch");
    request.Headers.Add("content-type", "text/plain");

    using var response = await Fetch.FetchAsync(request);
    return HttpResponse.Text(await response.ReadAsStringAsync(), response.StatusCode);
});

builder.MapGet("/fetch/headers", static async c =>
{
    using var response = await Fetch.FetchAsync(Upstream(c) + "/headers");
    var headers = response.ReadHeaders();
    return HttpResponse.Json(
        new FetchHeadersInfo(response.StatusCode, headers.Get("x-upstream"), headers.Get("x-multi"), headers.Get("content-type")),
        E2EJsonContext.Default.FetchHeadersInfo);
});

builder.MapGet("/fetch/redirect/:mode", static async c =>
{
    var request = new FetchRequestMessage(Upstream(c) + "/redirect")
    {
        Redirect = c.Parameters["mode"] switch
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

builder.MapGet("/fetch/bytes", static async c =>
{
    using var response = await Fetch.FetchAsync(Upstream(c) + "/bytes");
    return HttpResponse.Binary(await response.ReadAsBytesAsync());
});

builder.MapGet("/fetch/json", static async c =>
{
    using var response = await Fetch.FetchAsync(Upstream(c) + "/json");
    var person = await response.ReadAsJsonAsync(E2EJsonContext.Default.Person);
    return HttpResponse.Json(person!, E2EJsonContext.Default.Person);
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

// -- handle lifetime -----------------------------------------------------------------------

// Creates and releases many JS handles in a single request to shake out leaks / double frees.
builder.MapGet("/stress/handles/:n", static async c =>
{
    int n = int.Parse(c.Parameters["n"]);
    using var kv = c.Env.Kv("E2E_KV");
    for (int i = 0; i < n; i++)
    {
        await kv.PutAsync("stress:" + (i % 10), "value-" + i);
        _ = await kv.GetTextAsync("stress:" + (i % 10));
    }

    return HttpResponse.Text(n.ToString());
});

builder.Build().Run();

static async Task PutAfterDelayAsync(KvNamespace kv, string key)
{
    try
    {
        await WorkerTimer.Delay(300);
        await kv.PutAsync(key, "done-after-response");
    }
    finally
    {
        kv.Dispose();
    }
}


static string Upstream(HttpContext c)
    => c.Env.Var("UPSTREAM_URL") ?? throw new InvalidOperationException("UPSTREAM_URL is not configured.");

static string? Query(HttpContext c, string name)
{
    string query = c.Request.Uri.Query;
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

[JsonSerializable(typeof(Person))]
[JsonSerializable(typeof(HeaderPair[]))]
[JsonSerializable(typeof(EnvInfo))]
[JsonSerializable(typeof(KvListInfo))]
[JsonSerializable(typeof(R2Info))]
[JsonSerializable(typeof(Item))]
[JsonSerializable(typeof(ItemRow[]))]
[JsonSerializable(typeof(D1RunInfo))]
[JsonSerializable(typeof(FetchHeadersInfo))]
public sealed partial class E2EJsonContext : JsonSerializerContext;
