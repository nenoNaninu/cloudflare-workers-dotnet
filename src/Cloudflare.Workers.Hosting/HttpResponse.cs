using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Cloudflare.Workers.Hosting.Interop;

namespace Cloudflare.Workers.Hosting;

public sealed class HttpResponse
{
    private string? _textBody;
    private byte[]? _bytesBody;

    private HttpResponse(int statusCode)
    {
        StatusCode = statusCode;
    }

    public int StatusCode { get; set; }

    public HttpHeaders Headers { get; } = new();

    public static HttpResponse Text(string body, int status = 200, string contentType = "text/plain; charset=utf-8")
    {
        var response = new HttpResponse(status)
        {
            _textBody = body
        };

        response.Headers.Replace("content-type", contentType);

        return response;
    }

    public static HttpResponse Binary(byte[] body, int status = 200, string contentType = "application/octet-stream")
    {
        var response = new HttpResponse(status)
        {
            _bytesBody = body
        };

        response.Headers.Replace("content-type", contentType);

        return response;
    }

    public static HttpResponse Json(byte[] body, int status = 200, string contentType = "application/json; charset=utf-8")
        => Binary(body, status, contentType);

    public static HttpResponse Json(string json, int status = 200, string contentType = "application/json; charset=utf-8")
        => Text(json, status, contentType);

    public static HttpResponse Json<T>(T value, JsonTypeInfo<T> typeInfo, int status = 200, string contentType = "application/json; charset=utf-8")
        => Binary(JsonSerializer.SerializeToUtf8Bytes(value, typeInfo), status, contentType);

    public static HttpResponse Html(string html, int status = 200, string contentType = "text/html; charset=utf-8")
        => Text(html, status, contentType);

    public static HttpResponse Empty(int status = 204) => new(status);

    public static HttpResponse Error(string message, int status = 500) => Text(message, status);

    public static HttpResponse NotFound(string message = "Not Found") => Text(message, 404);

    public static HttpResponse Redirect(string location, int status = 302)
    {
        var response = new HttpResponse(status);
        response.Headers.Replace("location", location);
        return response;
    }

    public JsObject ToJsObject(JsRuntime runtime)
    {
        using var init = runtime.NewObject();

        init.SetProperty("status", JsArg.From(StatusCode));

        using (var headerPairs = Headers.ToJsObject(runtime))
        {
            init.SetProperty("headers", JsArg.From(headerPairs));
        }

        var body = _textBody is not null ? JsArg.From(_textBody)
            : _bytesBody is not null ? JsArg.From(_bytesBody)
            : JsArg.Null;

        return runtime.Construct("Response", body, JsArg.From(init));
    }
}
