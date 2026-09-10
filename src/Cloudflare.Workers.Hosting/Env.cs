using Cloudflare.Workers.Hosting.Interop;

namespace Cloudflare.Workers.Hosting;

public sealed class Env : IDisposable
{
    private readonly JsObject _js;

    internal Env(JsObject js)
    {
        _js = js;
    }

    public JsObject Js => _js;

    public bool HasBinding(string name)
    {
        using var binding = _js.GetProperty(name);
        return !binding.IsNullOrUndefined;
    }

    public string? Var(string name) => _js.GetPropertyAsString(name);

    public string? Secret(string name) => _js.GetPropertyAsString(name);

    public KvNamespace Kv(string binding) => new(GetBinding(binding));

    public R2Bucket R2(string binding) => new(GetBinding(binding));

    public D1Database D1(string binding) => new(GetBinding(binding));

    public ServiceBinding Service(string binding) => new(GetBinding(binding));

    private JsObject GetBinding(string name)
    {
        var binding = _js.GetProperty(name);

        if (binding.IsNullOrUndefined)
        {
            binding.Dispose();
            throw new InvalidOperationException($"Binding '{name}' was not found on env. Check the wrangler configuration.");
        }

        return binding;
    }

    public void Dispose() => _js.Dispose();
}
