using System.Diagnostics;
using Hispania.Platform.Contracts.Observability;
using Hispania.Platform.Observability.Application;

namespace Hispania.Server;

public sealed class PlatformCorrelationMiddleware(RequestDelegate next, ILogger<PlatformCorrelationMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext http, IPlatformMetricsV1 metrics, IConfiguration configuration)
    {
        var values = http.Request.Headers[PlatformCorrelationV1.HeaderName];
        if (values.Count > 1 || values.Count == 1 && !PlatformCorrelationV1.IsValidId(values[0]))
        {
            http.Response.StatusCode = StatusCodes.Status400BadRequest;
            await http.Response.WriteAsJsonAsync(new { error = "invalid_correlation_id" });
            return;
        }
        var correlation = configuration.GetValue("Platform:Observability:AcceptClientCorrelation", true) && values.Count == 1 ? values[0]! : Guid.NewGuid().ToString("N");
        using var scope = PlatformCorrelationV1.Push(new(correlation, OperationId: http.TraceIdentifier, TraceParent: Activity.Current?.Id, TraceState: Activity.Current?.TraceStateString));
        // Existing endpoint adapters receive the canonical value until they migrate to the context API.
        http.Request.Headers[PlatformCorrelationV1.HeaderName] = correlation;
        http.Response.OnStarting(() => { http.Response.Headers[PlatformCorrelationV1.HeaderName] = correlation; return Task.CompletedTask; });
        using var logging = PlatformLogConventionsV1.BeginOperation(logger, "HTTP", http.Request.Method);
        using var activity = PlatformTracingV1.Start("http.request", ActivityKind.Server);
        var started = Stopwatch.GetTimestamp();
        try { await next(http); }
        finally { metrics.BestEffort(sink => sink.Observe("hispania.http.duration", Stopwatch.GetElapsedTime(started).TotalMilliseconds)); }
    }
}
