using Microsoft.AspNetCore.Http.Features;

namespace Hispania.Server;

public sealed class SecurityRequestMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext http, SecurityAbuseLimiter limiter)
    {
        http.Response.OnStarting(() =>
        {
            http.Response.Headers["Referrer-Policy"] = "no-referrer";
            http.Response.Headers["X-Frame-Options"] = "DENY";
            http.Response.Headers["X-Content-Type-Options"] = "nosniff";
            http.Response.Headers["Content-Security-Policy"] = "default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' https://image.tmdb.org blob: data:; font-src 'self'; connect-src 'self'; media-src 'self' blob:; object-src 'none'; frame-src 'none'; frame-ancestors 'none'; base-uri 'self'; form-action 'self'";
            if (http.Request.Path.StartsWithSegments("/api/identity") || http.Request.Path.StartsWithSegments("/api/authorization") || http.Request.Path.StartsWithSegments("/api/platform/admin") || http.Request.Path.StartsWithSegments("/api/platform/maintenance") || http.Request.Path.StartsWithSegments("/internal"))
                http.Response.Headers.CacheControl = "no-store";
            return Task.CompletedTask;
        });
        // Forwarded headers are deliberately not processed: only the socket peer is trusted.
        var auth = http.Request.Path.Equals("/api/identity/login") || http.Request.Path.Equals("/api/authorization/bootstrap-admin");
        if (http.Request.Path.StartsWithSegments("/api/platform/maintenance") && http.Request.Method == "POST")
        {
            const long maximumMaintenanceBytes = 8192;
            var size = http.Features.Get<IHttpMaxRequestBodySizeFeature>();
            if (size is { IsReadOnly: false }) size.MaxRequestBodySize = maximumMaintenanceBytes;
            if (http.Request.ContentLength > maximumMaintenanceBytes) { http.Response.StatusCode = 413; return; }
        }
        if (auth)
        {
            const long maximumBytes = 8192;
            var feature = http.Features.Get<IHttpMaxRequestBodySizeFeature>();
            if (feature is { IsReadOnly: false }) feature.MaxRequestBodySize = maximumBytes;
            if (http.Request.ContentLength > maximumBytes) { http.Response.StatusCode = 413; return; }
            var retry = limiter.Acquire(http.Connection.RemoteIpAddress?.ToString() ?? "unknown");
            if (retry > 0) { await SecurityAbuseLimiter.Rejected(http, retry).ExecuteAsync(http); return; }
        }
        await next(http);
    }
}
