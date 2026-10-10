using Hispania.Platform.Contracts.Audit;
using Hispania.Platform.Contracts.Authorization;
using Hispania.Platform.Contracts.Identity;
using Hispania.Platform.Contracts.Observability;

namespace Hispania.Server;

public static class AuthorizationEndpointFilters
{
    public sealed record PermissionPolicy(string PermissionId, bool AllowLegacyAdminBearer);
    public static TBuilder RequirePlatformPermission<TBuilder>(this TBuilder builder, string permissionId, bool allowLegacyAdminBearer = true) where TBuilder : IEndpointConventionBuilder
    {
        builder.WithMetadata(new PermissionPolicy(permissionId, allowLegacyAdminBearer));
        builder.AddEndpointFilter(RequirePermission(permissionId, allowLegacyAdminBearer));
        return builder;
    }
    private static readonly Action<ILogger, string, PathString, Exception?> LegacyAdminBearerUsed = LoggerMessage.Define<string, PathString>(LogLevel.Warning, new EventId(1302, "LegacyAdminBearerUsed"), "Deprecated admin bearer used for protected endpoint {PermissionId} at {Path}.");
    private static readonly object UserKey = new();
    private static readonly object LegacyKey = new();

    public static IEndpointFilter RequirePermission(string permissionId, bool allowLegacyAdminBearer = true) => new PermissionFilter(permissionId, allowLegacyAdminBearer);

    private sealed class PermissionFilter(string permissionId, bool allowLegacyAdminBearer) : IEndpointFilter
    {
        public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
        {
            var http = context.HttpContext;
            var config = http.RequestServices.GetRequiredService<IConfiguration>();
            var correlationId = http.Request.Headers["X-Correlation-ID"].FirstOrDefault() ?? System.Diagnostics.Activity.Current?.TraceId.ToString();
            var authorizationHeader = http.Request.Headers.Authorization.ToString();

            if (allowLegacyAdminBearer && await PlatformEndpoints.IsAdminAsync(http, config, http.RequestAborted).ConfigureAwait(false))
            {
                http.Items[LegacyKey] = true;
                LegacyAdminBearerUsed(http.RequestServices.GetRequiredService<ILogger<PermissionFilter>>(), permissionId, http.Request.Path, null);
                return await next(context).ConfigureAwait(false);
            }

            var token = authorizationHeader.StartsWith("Session ", StringComparison.OrdinalIgnoreCase) ? authorizationHeader[8..].Trim() : string.Empty;
            if (token.Length is < 32 or > 256)
            {
                await WriteDeniedAuditAsync(http, correlationId, permissionId, null).ConfigureAwait(false);
                return Results.Unauthorized();
            }
            var identity = http.RequestServices.GetRequiredService<IIdentityQueryV1>();
            var session = token.Length is >= 32 and <= 256 ? await identity.ValidateSessionAsync(token, http.RequestAborted).ConfigureAwait(false) : null;
            if (session is null)
            {
                await WriteDeniedAuditAsync(http, correlationId, permissionId, null).ConfigureAwait(false);
                return Results.Unauthorized();
            }

            var authorization = http.RequestServices.GetRequiredService<IAuthorizationServiceV1>();
            var decision = await authorization.RequirePermissionAsync(session.UserId, permissionId, http.RequestAborted).ConfigureAwait(false);
            if (!decision.Authenticated)
            {
                await WriteDeniedAuditAsync(http, correlationId, permissionId, session.UserId).ConfigureAwait(false);
                return Results.Unauthorized();
            }
            if (!decision.Allowed)
            {
                await WriteDeniedAuditAsync(http, correlationId, permissionId, session.UserId).ConfigureAwait(false);
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }

            http.Items[UserKey] = session.UserId;
            var result = await next(context).ConfigureAwait(false);
            if (permissionId == Hispania.Library.Contracts.MediaPermissionIds.Admin && !HttpMethods.IsGet(http.Request.Method))
            {
                var audit = http.RequestServices.GetService<IPlatformAuditWriter>();
                if (audit is not null)
                    await audit.WriteAsync(new(DateTimeOffset.UtcNow, session.UserId.Value.ToString("D"), correlationId, "media", $"{http.Request.Method.ToLowerInvariant()} {http.Request.Path}", "media-operation", http.Request.Path, "attempted", new Dictionary<string, string>()), http.RequestAborted).ConfigureAwait(false);
            }
            return result;
        }
    }

    public static UserId? AuthenticatedUserId(HttpContext http) => http.Items.TryGetValue(UserKey, out var value) && value is UserId id ? id : null;
    public static bool IsLegacyAdmin(HttpContext http) => http.Items.ContainsKey(LegacyKey);

    private static async ValueTask WriteDeniedAuditAsync(HttpContext http, string? correlationId, string permissionId, UserId? userId)
    {
        var metrics = http.RequestServices.GetService<Hispania.Platform.Contracts.Observability.IPlatformMetricsV1>();
        metrics.BestEffort(sink => sink.Increment("hispania.security.authorization.denied"));
        if (http.RequestServices.GetService<SecurityAbuseLimiter>() is { } limiter && !limiter.TryRecordDeniedAudit())
        {
            metrics.BestEffort(sink => sink.Increment("hispania.security.audit.denial_suppressed"));
            return;
        }
        var audit = http.RequestServices.GetService<IPlatformAuditWriter>();
        if (audit is null) return;
        await audit.WriteAsync(new(DateTimeOffset.UtcNow, userId?.Value.ToString("D"), correlationId, "platform.authorization", "authorization-denied", "permission", permissionId, "failure", new Dictionary<string, string>()), http.RequestAborted).ConfigureAwait(false);
    }
}
