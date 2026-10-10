using System.Net;
using Cloudflare.Workers.Hosting.Interop;

namespace Cloudflare.Workers.Hosting;

public sealed class FetchHttpMessageHandler : HttpMessageHandler
{
    private readonly JsRuntime? _runtime;

    public FetchHttpMessageHandler()
    {
    }

    public FetchHttpMessageHandler(JsRuntime runtime)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        _runtime = runtime;
    }

    /// <summary>Whether redirects are followed automatically. Defaults to <see langword="true"/>.</summary>
    public bool AllowAutoRedirect { get; set; } = true;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.RequestUri is not { IsAbsoluteUri: true } uri)
        {
            throw new InvalidOperationException("The request URI must be an absolute URI.");
        }

        cancellationToken.ThrowIfCancellationRequested();

        byte[]? body = null;

        if (request.Content is not null)
        {
            body = await request.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        }

        var fetchRequest = new FetchRequestMessage(uri.AbsoluteUri)
        {
            Method = request.Method.Method,
            // fetch() rejects a body on GET/HEAD.
            BytesBody = body is { Length: > 0 } ? body : null,
            Redirect = AllowAutoRedirect ? FetchRedirectMode.Follow : FetchRedirectMode.Manual,
        };

        foreach (var header in request.Headers)
        {
            fetchRequest.Headers.Add(header.Key, string.Join(", ", header.Value));
        }

        if (request.Content is not null)
        {
            foreach (var header in request.Content.Headers)
            {
                // Content-Length is computed by the runtime from the body.
                if (string.Equals(header.Key, "Content-Length", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                fetchRequest.Headers.Add(header.Key, string.Join(", ", header.Value));
            }
        }

        using var fetchResponse = await Fetch.FetchAsync(_runtime ?? JsRuntime.Current, fetchRequest).ConfigureAwait(false);

        var responseBody = await fetchResponse.ReadAsBytesAsync().ConfigureAwait(false);
        var responseHeaders = fetchResponse.ReadHeaders();

        var response = new HttpResponseMessage((HttpStatusCode)fetchResponse.StatusCode)
        {
            RequestMessage = request,
            Content = new ByteArrayContent(responseBody),
        };

        foreach (var (name, value) in responseHeaders)
        {
            // The runtime has already decoded the body and the size no longer matches the wire format.
            if (string.Equals(name, "Content-Encoding", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "Content-Length", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!response.Headers.TryAddWithoutValidation(name, value))
            {
                response.Content.Headers.TryAddWithoutValidation(name, value);
            }
        }

        return response;
    }
}
