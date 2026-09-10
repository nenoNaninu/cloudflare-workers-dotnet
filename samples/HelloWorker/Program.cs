using System.Text.Json.Serialization;
using Cloudflare.Workers.Hosting;
using Cloudflare.Workers.Hosting.Interop;

var builder = WorkerApplication.CreateBuilder();

builder.MapGet("/", static _ =>
    Task.FromResult(HttpResponse.Text("Hello from C# on Cloudflare Workers!")));

builder.MapGet("/datetime", static _ =>
{
    var now = DateTime.UtcNow;
    return Task.FromResult(HttpResponse.Text(now.ToString("O")));
});

builder.MapGet("/json", static context =>
{
    var request = context.Request;
    var info = new RequestInfo(
        request.Method,
        request.Url,
        request.Headers.Get("user-agent"),
        request.GetCfPropertyAsString("colo"));
    return Task.FromResult(HttpResponse.Json(info, WorkerJsonContext.Default.RequestInfo));
});

// SAMPLE_VAR is configured in wrangler.jsonc; SAMPLE_SECRET comes from a Worker secret.
builder.MapGet("/env", static context =>
{
    var info = new EnvInfo(
        context.Env.Var("SAMPLE_VAR"),
        context.Env.Secret("SAMPLE_SECRET"));
    return Task.FromResult(HttpResponse.Json(info, WorkerJsonContext.Default.EnvInfo));
});

builder.MapPost("/echo", static async context =>
    HttpResponse.Text(await context.Request.ReadAsStringAsync()));

builder.MapGet("/delay/:ms", static async context =>
{
    double ms = Math.Min(double.Parse(context.Parameters["ms"]), 5000);
    await WorkerTimer.Delay(ms);
    return HttpResponse.Text($"Slept {ms} ms without blocking the isolate.");
});

// Returns the response immediately while the registered task continues after it.
builder.MapGet("/wait-until", static context =>
{
    context.WorkerContext.WaitUntil(LogAfterResponseAsync());
    return Task.FromResult(HttpResponse.Text("Response sent. Check the Worker logs in one second."));
});

// Requires a KV namespace bound as HELLO_KV in wrangler.jsonc.
builder.MapGet("/kv/:key", static async context =>
{
    if (!context.Env.HasBinding("HELLO_KV"))
    {
        return HttpResponse.Error("KV binding HELLO_KV is not configured.", 503);
    }

    using var kv = context.Env.Kv("HELLO_KV");
    string key = context.Parameters["key"];
    string? value = await kv.GetTextAsync(key);
    return value is null
        ? HttpResponse.NotFound($"Key '{key}' not found.")
        : HttpResponse.Text(value);
});

builder.MapPut("/kv/:key", static async context =>
{
    if (!context.Env.HasBinding("HELLO_KV"))
    {
        return HttpResponse.Error("KV binding HELLO_KV is not configured.", 503);
    }

    using var kv = context.Env.Kv("HELLO_KV");
    await kv.PutAsync(context.Parameters["key"], await context.Request.ReadAsStringAsync());
    return HttpResponse.Empty();
});

// Requires a D1 database bound as DB and the migrations to be applied.
builder.MapGet("/d1/messages", static async context =>
{
    if (!context.Env.HasBinding("DB"))
    {
        return HttpResponse.Error("D1 binding DB is not configured.", 503);
    }

    using var db = context.Env.D1("DB");
    using var statement = db.Prepare(
        "SELECT id, text, created_at FROM messages ORDER BY id DESC LIMIT 100");
    MessageRow[] messages = await statement.AllAsync(
        WorkerJsonContext.Default.MessageRowArray) ?? [];
    return HttpResponse.Json(messages, WorkerJsonContext.Default.MessageRowArray);
});

builder.MapPost("/d1/messages", static async context =>
{
    if (!context.Env.HasBinding("DB"))
    {
        return HttpResponse.Error("D1 binding DB is not configured.", 503);
    }

    CreateMessageRequest? input = await context.Request.ReadAsJsonAsync(
        WorkerJsonContext.Default.CreateMessageRequest);
    string text = (input?.Text ?? string.Empty).Trim();
    if (text.Length is 0 or > 500)
    {
        return HttpResponse.Error("text must contain between 1 and 500 characters.", 400);
    }

    using var db = context.Env.D1("DB");
    string id = Guid.NewGuid().ToString();
    using (var insert = db
        .Prepare("INSERT INTO messages (id, text) VALUES (?1, ?2)")
        .Bind(JsArg.From(id), JsArg.From(text)))
    {
        D1Result result = await insert.RunAsync();
        if (!result.Success)
        {
            return HttpResponse.Error("Failed to insert the message.");
        }

        using var select = db
            .Prepare("SELECT id, text, created_at FROM messages WHERE id = ?1")
            .Bind(JsArg.From(id));
        string? created = await select.FirstJsonAsync();
        return created is null
            ? HttpResponse.Error("The inserted message could not be read back.")
            : HttpResponse.Json(created, 201);
    }
});

// Outbound fetch through the Workers runtime.
builder.MapGet("/proxy", static async _ =>
{
    using var upstream = await Fetch.FetchAsync(new FetchRequestMessage("https://example.com/"));
    var body = await upstream.ReadAsBytesAsync();

    return HttpResponse.Binary(
        body,
        upstream.StatusCode,
        upstream.Headers.Get("content-type") ?? "text/html; charset=utf-8"
    );
});

builder.Build().Run();

static async Task LogAfterResponseAsync()
{
    await WorkerTimer.Delay(TimeSpan.FromSeconds(10));
    Console.WriteLine("WaitUntil task completed after the response was sent!!!!!!!!!");
}

public sealed record RequestInfo(string Method, string Url, string? UserAgent, string? Colo);

public sealed record EnvInfo(
    [property: JsonPropertyName("sampleVar")] string? SampleVar,
    [property: JsonPropertyName("sampleSecretConfigured")] string? SecretVar);

public sealed record CreateMessageRequest(
    [property: JsonPropertyName("text")] string Text);

public sealed record MessageRow(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("text")] string Text,
    [property: JsonPropertyName("created_at")] string CreatedAt);

[JsonSerializable(typeof(RequestInfo))]
[JsonSerializable(typeof(EnvInfo))]
[JsonSerializable(typeof(CreateMessageRequest))]
[JsonSerializable(typeof(MessageRow[]))]
public sealed partial class WorkerJsonContext : JsonSerializerContext;
