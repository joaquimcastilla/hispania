using Hispania.Platform.Contracts.Audit;
using Hispania.Platform.Contracts.Authorization;
using Hispania.Platform.Contracts.Identity;

namespace Hispania.Server;

public static class AuthorizationEndpointFilters
{
    private static readonly Action<ILogger, string, PathString, Exception?> LegacyAdminBearerUsed = LoggerMessage.Define<string, PathString>(LogLevel.Warning, new EventId(1302, "LegacyAdminBearerUsed"), "Deprecated admin bearer used for Platform endpoint {PermissionId} at {Path}.");
    private static readonly object UserKey = new();
    private static readonly object LegacyKey = new();

    public static IEndpointFilter RequirePermission(string permissionId) => new PermissionFilter(permissionId);

    private sealed class PermissionFilter(string permissionId) : IEndpointFilter
    {
        public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
        {
            var http = context.HttpContext;
            var config = http.RequestServices.GetRequiredService<IConfiguration>();
            var correlationId = http.Request.Headers["X-Correlation-ID"].FirstOrDefault() ?? System.Diagnostics.Activity.Current?.TraceId.ToString();
            var authorizationHeader = http.Request.Headers.Authorization.ToString();

            if (await PlatformEndpoints.IsAdminAsync(http, config, http.RequestAborted).ConfigureAwait(false))
            {
                http.Items[LegacyKey] = true;
                LegacyAdminBearerUsed(http.RequestServices.GetRequiredService<ILogger<PermissionFilter>>(), permissionId, http.Request.Path, null);
                return await next(context).ConfigureAwait(false);
            }

            var token = authorizationHeader.StartsWith("Session ", StringComparison.OrdinalIgnoreCase) ? authorizationHeader[8..].Trim() : string.Empty;
            var identity = http.RequestServices.GetRequiredService<IIdentityQueryV1>();
            var session = token.Length is >= 32 and <= 256 ? await identity.ValidateSessionAsync(token, http.RequestAborted).ConfigureAwait(false) : null;
            if (session is null)
            {
                await WriteDeniedAuditAsync(http, correlationId, permissionId, null).ConfigureAwait(false);
                return Results.Unauthorized();
            }

            var authorization = http.RequestServices.GetRequiredService<IAuthorizationServiceV1>();
            var decision = await authorization.RequirePermissionAsync(session.UserId, permissionId, http.RequestAborted).ConfigureAwait(false);
            if (!decision.Allowed)
            {
                await WriteDeniedAuditAsync(http, correlationId, permissionId, session.UserId).ConfigureAwait(false);
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }

            http.Items[UserKey] = session.UserId;
            return await next(context).ConfigureAwait(false);
        }
    }

    public static UserId? AuthenticatedUserId(HttpContext http) => http.Items.TryGetValue(UserKey, out var value) && value is UserId id ? id : null;
    public static bool IsLegacyAdmin(HttpContext http) => http.Items.ContainsKey(LegacyKey);

    private static async ValueTask WriteDeniedAuditAsync(HttpContext http, string? correlationId, string permissionId, UserId? userId)
    {
        var audit = http.RequestServices.GetService<IPlatformAuditWriter>();
        if (audit is null) return;
        await audit.WriteAsync(new(DateTimeOffset.UtcNow, userId?.Value.ToString("D"), correlationId, "platform.authorization", "authorization-denied", "permission", permissionId, "failure", new Dictionary<string, string>()), http.RequestAborted).ConfigureAwait(false);
    }
}
