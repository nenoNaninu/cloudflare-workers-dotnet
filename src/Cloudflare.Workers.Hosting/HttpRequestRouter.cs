namespace Cloudflare.Workers.Hosting;

internal sealed class HttpRequestRouter
{
    private static readonly IReadOnlyDictionary<string, string> EmptyParams = new Dictionary<string, string>();

    private readonly List<Route> _routes = [];
    private HttpRequestHandler? _fallback;

    public HttpRequestRouter Get(string pattern, HttpRequestHandler handler) => Add("GET", pattern, handler);

    public HttpRequestRouter Post(string pattern, HttpRequestHandler handler) => Add("POST", pattern, handler);

    public HttpRequestRouter Put(string pattern, HttpRequestHandler handler) => Add("PUT", pattern, handler);

    public HttpRequestRouter Patch(string pattern, HttpRequestHandler handler) => Add("PATCH", pattern, handler);

    public HttpRequestRouter Delete(string pattern, HttpRequestHandler handler) => Add("DELETE", pattern, handler);

    public HttpRequestRouter Head(string pattern, HttpRequestHandler handler) => Add("HEAD", pattern, handler);

    public HttpRequestRouter Options(string pattern, HttpRequestHandler handler) => Add("OPTIONS", pattern, handler);

    /// <summary>Matches the pattern for any HTTP method.</summary>
    public HttpRequestRouter All(string pattern, HttpRequestHandler handler) => Add(null, pattern, handler);

    /// <summary>Handler invoked when no route matches (defaults to a plain 404).</summary>
    public HttpRequestRouter Fallback(HttpRequestHandler handler)
    {
        _fallback = handler;
        return this;
    }

    public Task<HttpResponse> HandleAsync(HttpRequest request, Env env, WorkerContext executionContext)
    {
        string path = request.Path;
        var requestSegments = SplitSegments(path);

        foreach (var route in _routes)
        {
            if (route.Method is not null
                && !string.Equals(route.Method, request.Method, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (TryMatch(route.Segments, requestSegments, out var parameters))
            {
                return route.Handler(new HttpContext(request, env, executionContext, parameters));
            }
        }

        if (_fallback is not null)
        {
            return _fallback(new HttpContext(request, env, executionContext, EmptyParams));
        }

        return Task.FromResult(HttpResponse.NotFound());
    }

    private HttpRequestRouter Add(string? method, string pattern, HttpRequestHandler handler)
    {
        _routes.Add(new Route(method, SplitSegments(pattern), handler));
        return this;
    }

    private static string[] SplitSegments(string path)
        => path.Split('/', StringSplitOptions.RemoveEmptyEntries);

    private static bool TryMatch(string[] pattern, string[] requestSegments, out IReadOnlyDictionary<string, string> parameters)
    {
        Dictionary<string, string>? captured = null;

        for (int i = 0; i < pattern.Length; i++)
        {
            string expected = pattern[i];

            if (expected.StartsWith('*'))
            {
                var rest = string.Join('/', requestSegments.Skip(i).Select(Uri.UnescapeDataString));
                captured ??= [];
                captured[expected.Length > 1 ? expected[1..] : "*"] = rest;
                parameters = captured;
                return true;
            }

            if (i >= requestSegments.Length)
            {
                parameters = EmptyParams;
                return false;
            }

            if (expected.StartsWith(':'))
            {
                captured ??= [];
                captured[expected[1..]] = Uri.UnescapeDataString(requestSegments[i]);
            }
            else if (!string.Equals(expected, requestSegments[i], StringComparison.Ordinal))
            {
                parameters = EmptyParams;
                return false;
            }
        }

        if (requestSegments.Length != pattern.Length)
        {
            parameters = EmptyParams;
            return false;
        }

        parameters = captured ?? EmptyParams;
        return true;
    }

    private sealed record Route(string? Method, string[] Segments, HttpRequestHandler Handler);
}
