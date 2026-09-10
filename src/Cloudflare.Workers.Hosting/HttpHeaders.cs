using System.Collections;
using Cloudflare.Workers.Hosting.Interop;

namespace Cloudflare.Workers.Hosting;

public sealed class HttpHeaders : IEnumerable<KeyValuePair<string, string>>
{
    private readonly List<KeyValuePair<string, string>> _entries = [];

    public HttpHeaders()
    {
    }

    public HttpHeaders(IEnumerable<KeyValuePair<string, string>> entries)
    {
        foreach (var (name, value) in entries)
        {
            Add(name, value);
        }
    }

    public int Count => _entries.Count;

    public void Add(string name, string value)
        => _entries.Add(new(name, value));

    public void Replace(string name, string value)
    {
        Delete(name);
        Add(name, value);
    }

    public string? Get(string name)
    {
        var values = _entries
            .Where(entry => string.Equals(entry.Key, name, StringComparison.OrdinalIgnoreCase))
            .Select(entry => entry.Value);

        return values.Any() ? string.Join(", ", values) : null;
    }

    public bool Has(string name)
        => _entries.Any(e => string.Equals(e.Key, name, StringComparison.OrdinalIgnoreCase));

    public void Delete(string name)
        => _entries.RemoveAll(e => string.Equals(e.Key, name, StringComparison.OrdinalIgnoreCase));

    public IEnumerator<KeyValuePair<string, string>> GetEnumerator()
        => _entries.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public static HttpHeaders FromJsObject(JsObject jsHeaders)
    {
        var headers = new HttpHeaders();

        using var iterator = jsHeaders.Call("entries");
        using var array = ToArray(jsHeaders.Runtime, iterator);

        int length = array.GetArrayLength();

        for (int i = 0; i < length; i++)
        {
            using var pair = array.GetElement(i);

            using var name = pair.GetElement(0);
            using var value = pair.GetElement(1);

            headers.Add(name.AsString(), value.AsString());
        }

        return headers;
    }

    internal JsObject ToJsObject(JsRuntime runtime)
    {
        var array = runtime.NewArray();

        try
        {
            foreach (var (name, value) in _entries)
            {
                using var pair = runtime.NewArray(JsArg.From(name), JsArg.From(value));
                runtime.Interop.ArrayPush(array.Handle, pair.Handle);
            }

            return array;
        }
        catch
        {
            array.Dispose();
            throw;
        }
    }

    private static JsObject ToArray(JsRuntime runtime, JsObject iterator)
    {
        using var arrayGlobal = runtime.GetGlobal("Array");
        return arrayGlobal.Call("from", JsArg.From(iterator));
    }
}
