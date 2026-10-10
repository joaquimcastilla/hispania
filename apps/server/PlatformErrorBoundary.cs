namespace Hispania.Server;

/// <summary>Never serialize or log an exception supplied by a store, provider or extension.</summary>
public static class PlatformErrorBoundary
{
    private static readonly Action<ILogger, string, Exception?> Failed = LoggerMessage.Define<string>(
        LogLevel.Error, new EventId(13100, "PlatformRequestFailed"), "Platform request failed ({ErrorKind}).");

    public static IApplicationBuilder UsePlatformErrorBoundary(this IApplicationBuilder app) => app.Use(async (context, next) =>
    {
        try { await next(context).ConfigureAwait(false); }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { context.Abort(); }
        catch (Exception exception)
        {
            Failed(context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("Hispania.Platform.Request"), exception.GetType().Name, null);
            if (context.Response.HasStarted) { context.Abort(); return; }
            context.Response.Clear();
            var status = exception is BadHttpRequestException bad ? bad.StatusCode : StatusCodes.Status500InternalServerError;
            var correlation = context.Request.Headers[Hispania.Platform.Contracts.Observability.PlatformCorrelationV1.HeaderName].FirstOrDefault();
            if (!Hispania.Platform.Contracts.Observability.PlatformCorrelationV1.IsValidId(correlation)) correlation = Guid.NewGuid().ToString("N");
            await Results.Problem(title: "Request could not be completed.", statusCode: status, extensions: new Dictionary<string, object?> { ["code"] = "request_failed", ["correlationId"] = correlation }).ExecuteAsync(context).ConfigureAwait(false);
        }
    });
}
