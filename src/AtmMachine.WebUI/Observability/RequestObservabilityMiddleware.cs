using System.Diagnostics;

namespace AtmMachine.WebUI.Observability;

public sealed class RequestObservabilityMiddleware
{
    private const string CorrelationHeader = "X-Correlation-ID";
    private const string TraceHeader = "X-Trace-ID";

    private readonly RequestDelegate _next;
    private readonly ILogger<RequestObservabilityMiddleware> _logger;

    public RequestObservabilityMiddleware(
        RequestDelegate next,
        ILogger<RequestObservabilityMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context, BankingTelemetry telemetry)
    {
        string correlationId = ResolveCorrelationId(context);
        context.TraceIdentifier = correlationId;

        Activity? currentActivity = Activity.Current;
        Activity? createdActivity = null;
        if (currentActivity is null)
        {
            createdActivity = telemetry.ActivitySource.StartActivity(
                $"HTTP {context.Request.Method}",
                ActivityKind.Server);
            currentActivity = createdActivity;
        }

        currentActivity?.SetTag("correlation.id", correlationId);
        currentActivity?.SetTag("http.method", context.Request.Method);
        currentActivity?.SetTag("url.path", context.Request.Path.Value ?? "/");
        string? traceId = currentActivity?.TraceId.ToString();

        context.Response.OnStarting(() =>
        {
            context.Response.Headers[CorrelationHeader] = correlationId;
            if (!string.IsNullOrWhiteSpace(traceId))
            {
                context.Response.Headers[TraceHeader] = traceId;
            }

            return Task.CompletedTask;
        });

        long startTimestamp = Stopwatch.GetTimestamp();
        using IDisposable? scope = _logger.BeginScope(new Dictionary<string, object?>
        {
            ["CorrelationId"] = correlationId,
            ["TraceId"] = traceId,
            ["SpanId"] = currentActivity?.SpanId.ToString()
        });

        try
        {
            await _next(context);

            double elapsedMs = Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;
            currentActivity?.SetTag("http.status_code", context.Response.StatusCode);
            telemetry.RecordHttpRequest(
                context.Request.Method,
                context.Request.Path.Value ?? "/",
                context.Response.StatusCode,
                elapsedMs,
                correlationId);

            _logger.LogInformation(
                "HTTP request completed. Method={Method} Path={Path} StatusCode={StatusCode} ElapsedMs={ElapsedMs}",
                context.Request.Method,
                context.Request.Path.Value ?? "/",
                context.Response.StatusCode,
                Math.Round(elapsedMs, 2));
        }
        catch (Exception exception)
        {
            double elapsedMs = Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;
            currentActivity?.SetStatus(ActivityStatusCode.Error, exception.Message);
            telemetry.RecordHttpRequest(
                context.Request.Method,
                context.Request.Path.Value ?? "/",
                StatusCodes.Status500InternalServerError,
                elapsedMs,
                correlationId);

            _logger.LogError(
                exception,
                "HTTP request failed. Method={Method} Path={Path} ElapsedMs={ElapsedMs}",
                context.Request.Method,
                context.Request.Path.Value ?? "/",
                Math.Round(elapsedMs, 2));

            throw;
        }
        finally
        {
            createdActivity?.Dispose();
        }
    }

    private static string ResolveCorrelationId(HttpContext context)
    {
        string? incoming = context.Request.Headers[CorrelationHeader].ToString();
        return string.IsNullOrWhiteSpace(incoming)
            ? Guid.NewGuid().ToString("N")
            : incoming.Trim();
    }
}
