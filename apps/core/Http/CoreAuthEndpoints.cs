using System.Text.Json.Serialization;
using Hispania.Core.Identity;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Logging;

namespace Hispania.Core.Http;

internal static class CoreAuthEndpoints
{
    private const string SessionCookie = "__Secure-HispaniaSession";
    private static readonly TimeSpan SessionLifetime = TimeSpan.FromHours(12);
    private static readonly string[] ExposedPermissions =
    [
        CoreIdentityRules.IdentityReadPermission,
        CoreIdentityRules.IdentityManagePermission,
        CoreIdentityRules.AuthorizationManagePermission,
        CoreIdentityRules.SessionRevokePermission
    ];
    private static readonly Action<ILogger, string, Exception?> LoginRejected = LoggerMessage.Define<string>(
        LogLevel.Warning, new EventId(20, "CoreLoginRejected"), "Core login rejected. CorrelationId {CorrelationId}.");
    private static readonly Action<ILogger, string, Exception?> LoginSucceeded = LoggerMessage.Define<string>(
        LogLevel.Information, new EventId(21, "CoreLoginSucceeded"), "Core login succeeded. CorrelationId {CorrelationId}.");
    private static readonly Action<ILogger, string, Exception?> LogoutCompleted = LoggerMessage.Define<string>(
        LogLevel.Information, new EventId(22, "CoreLogoutCompleted"), "Core logout completed. CorrelationId {CorrelationId}.");

    internal static void Map(WebApplication app)
    {
        app.MapPost("/api/v1/auth/login", LoginAsync)
            .WithName("CoreLogin").RequireRateLimiting("core-login")
            .Produces<LoginResponse>(StatusCodes.Status200OK, "application/json")
            .Produces(StatusCodes.Status400BadRequest).Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status429TooManyRequests);
        app.MapPost("/api/v1/auth/logout", LogoutAsync)
            .WithName("CoreLogout").Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status400BadRequest).Produces(StatusCodes.Status401Unauthorized);
        app.MapGet("/api/v1/auth/me", MeAsync)
            .WithName("CoreMe").Produces<MeResponse>(StatusCodes.Status200OK, "application/json")
            .Produces(StatusCodes.Status401Unauthorized);
    }

    private static async Task<IResult> LoginAsync(
        LoginRequest? request,
        HttpContext context,
        ICoreIdentityService identities,
        ICoreSessionService sessions,
        ICoreHttpAuditService audit,
        IAntiforgery antiforgery,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        if (!HasSameOrigin(context)) return Results.StatusCode(StatusCodes.Status403Forbidden);
        if (request is null || request.Username is null || request.Password is null ||
            request.Username.Length > 64 || request.Password.Length > 256)
            return Results.BadRequest(new ErrorResponse("invalid_request"));

        var authentication = await identities.AuthenticateAsync(request.Username, request.Password, cancellationToken).ConfigureAwait(false);
        if (!authentication.Succeeded)
        {
            await audit.RecordAsync("http.login.rejected", null, cancellationToken).ConfigureAwait(false);
            LoginRejected(loggerFactory.CreateLogger("Hispania.Core.Security"), context.TraceIdentifier, null);
            return Results.Json(new ErrorResponse("authentication_failed"), statusCode: StatusCodes.Status401Unauthorized);
        }

        var session = await sessions.CreateAsync(authentication.Identity!.SubjectId, DateTimeOffset.UtcNow, SessionLifetime, cancellationToken).ConfigureAwait(false);
        if (session is null) return Results.Json(new ErrorResponse("authentication_failed"), statusCode: StatusCodes.Status401Unauthorized);
        try { await audit.RecordAsync("http.login.succeeded", authentication.Identity.SubjectId, cancellationToken).ConfigureAwait(false); }
        catch
        {
            await sessions.RevokeAsync(authentication.Identity.SubjectId, session.Token, DateTimeOffset.UtcNow, CancellationToken.None).ConfigureAwait(false);
            throw;
        }

        context.Response.Cookies.Append(SessionCookie, session.Token, new CookieOptions
        {
            HttpOnly = true,
            Secure = true,
            SameSite = SameSiteMode.Strict,
            Path = "/api/v1/auth",
            IsEssential = true,
            Expires = session.ExpiresUtc,
            MaxAge = SessionLifetime
        });
        var tokens = antiforgery.GetAndStoreTokens(context);
        LoginSucceeded(loggerFactory.CreateLogger("Hispania.Core.Security"), context.TraceIdentifier, null);
        return Results.Json(new LoginResponse(
            authentication.Identity.SubjectId,
            authentication.Identity.Username,
            tokens.RequestToken ?? string.Empty,
            session.ExpiresUtc));
    }

    private static async Task<IResult> LogoutAsync(
        HttpContext context,
        ICoreSessionService sessions,
        ICoreHttpAuditService audit,
        IAntiforgery antiforgery,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        var token = context.Request.Cookies[SessionCookie];
        var validation = await sessions.ValidateAsync(token, DateTimeOffset.UtcNow, cancellationToken).ConfigureAwait(false);
        if (!validation.IsValid || validation.SubjectId is null)
        {
            if (!string.IsNullOrEmpty(token)) await audit.RecordAsync("http.session.rejected", null, cancellationToken).ConfigureAwait(false);
            return Unauthorized();
        }
        if (!HasSameOrigin(context)) return Results.StatusCode(StatusCodes.Status403Forbidden);
        try { await antiforgery.ValidateRequestAsync(context).ConfigureAwait(false); }
        catch (AntiforgeryValidationException) { return Results.StatusCode(StatusCodes.Status400BadRequest); }
        var revoked = await sessions.RevokeAsync(validation.SubjectId.Value, token!, DateTimeOffset.UtcNow, cancellationToken).ConfigureAwait(false);
        if (revoked) await audit.RecordAsync("http.session.logout", validation.SubjectId.Value, cancellationToken).ConfigureAwait(false);
        context.Response.Cookies.Delete(SessionCookie, new CookieOptions { Secure = true, HttpOnly = true, SameSite = SameSiteMode.Strict, Path = "/api/v1/auth" });
        LogoutCompleted(loggerFactory.CreateLogger("Hispania.Core.Security"), context.TraceIdentifier, null);
        return revoked ? Results.NoContent() : Unauthorized();
    }

    private static async Task<IResult> MeAsync(
        HttpContext context,
        ICoreIdentityService identities,
        ICoreAuthorizationService authorization,
        ICoreSessionService sessions,
        ICoreHttpAuditService audit,
        CancellationToken cancellationToken)
    {
        var validation = await sessions.ValidateAsync(context.Request.Cookies[SessionCookie], DateTimeOffset.UtcNow, cancellationToken).ConfigureAwait(false);
        if (!validation.IsValid || validation.SubjectId is null)
        {
            if (context.Request.Cookies.ContainsKey(SessionCookie)) await audit.RecordAsync("http.session.rejected", null, cancellationToken).ConfigureAwait(false);
            return Unauthorized();
        }
        var identity = await identities.FindByIdAsync(validation.SubjectId.Value, cancellationToken).ConfigureAwait(false);
        if (identity is null || identity.Status != CoreAccountStatus.Active) return Unauthorized();
        var permissions = new List<string>();
        foreach (var permission in ExposedPermissions)
            if (await authorization.IsAuthorizedAsync(identity.SubjectId, permission, cancellationToken).ConfigureAwait(false)) permissions.Add(permission);
        return Results.Json(new MeResponse(identity.SubjectId, identity.Username, validation.ExpiresUtc, permissions));
    }

    private static bool HasSameOrigin(HttpContext context)
    {
        if (!context.Request.IsHttps || !context.Request.Headers.TryGetValue("Origin", out var originValues) || originValues.Count != 1)
            return false;
        if (!Uri.TryCreate(originValues[0], UriKind.Absolute, out var origin) || origin.Scheme != Uri.UriSchemeHttps ||
            !string.IsNullOrEmpty(origin.UserInfo) || origin.AbsolutePath != "/" || !string.IsNullOrEmpty(origin.Query) || !string.IsNullOrEmpty(origin.Fragment))
            return false;
        var expected = new UriBuilder(Uri.UriSchemeHttps, context.Request.Host.Host, context.Request.Host.Port ?? 443).Uri;
        return Uri.Compare(origin, expected, UriComponents.SchemeAndServer, UriFormat.Unescaped, StringComparison.OrdinalIgnoreCase) == 0;
    }

    private static IResult Unauthorized() => Results.Json(new ErrorResponse("authentication_required"), statusCode: StatusCodes.Status401Unauthorized);

    internal sealed record LoginRequest(string? Username, string? Password);
    internal sealed record LoginResponse(Guid SubjectId, string Username, string CsrfToken, DateTimeOffset ExpiresUtc);
    internal sealed record MeResponse(Guid SubjectId, string Username, DateTimeOffset? SessionExpiresUtc, IReadOnlyList<string> Permissions);
    internal sealed record ErrorResponse([property: JsonPropertyName("error")] string Code);
}
