using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Cloudflare.Workers.Hosting.Interop;

namespace Cloudflare.Workers.Hosting;

public sealed class D1Database : IDisposable
{
    private readonly JsObject _js;

    internal D1Database(JsObject js)
    {
        _js = js;
    }

    public JsObject Js => _js;

    public D1PreparedStatement Prepare(string sql)
    {
        return new D1PreparedStatement(_js.Call("prepare", JsArg.From(sql)));
    }

    public void Dispose() => _js.Dispose();
}

public sealed class D1PreparedStatement : IDisposable
{
    private readonly JsObject _js;

    internal D1PreparedStatement(JsObject js)
    {
        _js = js;
    }

    public D1PreparedStatement Bind(params JsArg[] parameters)
    {
        var next = _js.Call("bind", parameters);
        try
        {
            _js.Dispose();
            return new D1PreparedStatement(next);
        }
        catch
        {
            next.Dispose();
            throw;
        }
    }

    public async Task<D1Result> RunAsync()
    {
        using var result = await _js.CallAsync("run").ConfigureAwait(false);
        using var meta = result.GetProperty("meta");
        return new D1Result(
            result.GetPropertyAsBoolean("success") ?? true,
            meta.IsNullOrUndefined
                ? 0
                : (long)(meta.GetPropertyAsNumber("changes") ?? 0),
            meta.IsNullOrUndefined
                ? null
                : meta.GetPropertyAsNumber("last_row_id") is { } id
                    ? (long)id
                    : null
            );
    }

    public async Task<string> AllJsonAsync()
    {
        using var result = await _js.CallAsync("all").ConfigureAwait(false);
        using var rows = result.GetProperty("results");
        return _js.Runtime.JsonStringify(rows);
    }

    public async Task<T?> AllAsync<T>(JsonTypeInfo<T> typeInfo)
    {
        string json = await AllJsonAsync().ConfigureAwait(false);
        return JsonSerializer.Deserialize(json, typeInfo);
    }

    public async Task<string?> FirstJsonAsync()
    {
        using var row = await _js.CallAsync("first").ConfigureAwait(false);
        return row.IsNullOrUndefined ? null : _js.Runtime.JsonStringify(row);
    }

    public void Dispose() => _js.Dispose();
}

public sealed record D1Result(bool Success, long Changes, long? LastRowId);
