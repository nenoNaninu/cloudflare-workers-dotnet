namespace Cloudflare.Workers.Hosting;

public delegate Task<HttpResponse> HttpRequestHandler(HttpContext context);
