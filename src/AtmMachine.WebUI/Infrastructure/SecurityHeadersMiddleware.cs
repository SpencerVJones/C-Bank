namespace AtmMachine.WebUI.Infrastructure;

public sealed class SecurityHeadersMiddleware
{
    private const string ContentSecurityPolicy =
        "default-src 'self'; " +
        "base-uri 'self'; " +
        "object-src 'none'; " +
        "connect-src 'self' ws: wss:; " +
        "frame-ancestors 'none'; " +
        "form-action 'self'; " +
        "img-src 'self' data:; " +
        "style-src 'self' 'unsafe-inline'; " +
        "script-src 'self' https://cdnjs.cloudflare.com; " +
        "font-src 'self'";

    private readonly RequestDelegate _next;

    public SecurityHeadersMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        context.Response.OnStarting(() =>
        {
            IHeaderDictionary headers = context.Response.Headers;
            headers["Content-Security-Policy"] = ContentSecurityPolicy;
            headers["X-Content-Type-Options"] = "nosniff";
            headers["X-Frame-Options"] = "DENY";
            headers["Referrer-Policy"] = "no-referrer";
            headers["Permissions-Policy"] = "accelerometer=(), camera=(), geolocation=(), gyroscope=(), microphone=(), payment=(), usb=()";

            return Task.CompletedTask;
        });

        await _next(context);
    }
}
