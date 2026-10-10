using Hispania.Platform.Contracts.Identity;
using Hispania.Platform.Contracts.Authorization;

namespace Hispania.Server;

public static class IdentityEndpoints
{
    public static IEndpointRouteBuilder MapIdentityEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/identity").WithTags("Platform Identity");
        group.MapPost("/users", async (CreateUserRequest request, HttpContext http, IIdentityCommandV1 commands, CancellationToken ct) =>
        {
            var result = await commands.CreateUserAsync(request.Username, request.DisplayName, request.Password, Correlation(http), ct);
            if (!result.Succeeded) return result.Failure switch
            {
                IdentityFailure.DuplicateUsername => Results.Conflict(new { error = "username_unavailable" }),
                _ => Results.BadRequest(new { error = "invalid_user" })
            };
            return Results.Created($"/api/identity/users/{result.Value!.Id.Value:D}", result.Value);
        }).RequirePlatformPermission(PlatformPermissionIds.UsersWrite);

        group.MapPost("/login", async (LoginRequest request, HttpContext http, IIdentityCommandV1 commands, CancellationToken ct) =>
        {
            var limited = SecurityAbuseLimiter.CheckAccount(http, request.Username);
            if (limited is not null) return limited;
            var result = await commands.LoginAsync(request.Username, request.Password, Correlation(http), ct);
            return result.Succeeded ? Results.Ok(new { result.User, result.Session, result.SessionToken }) : Results.Json(new { error = "invalid_credentials" }, statusCode: StatusCodes.Status401Unauthorized);
        });

        group.MapGet("/me", async (HttpContext http, IIdentityQueryV1 query, IAuthorizationServiceV1 authorization, CancellationToken ct) =>
        {
            var token = SessionToken(http);
            if (token is null) return Results.Unauthorized();
            var session = await query.ValidateSessionAsync(token, ct);
            if (session is null) return Results.Unauthorized();
            var user = await query.GetUserAsync(session.UserId, ct);
            if (user is null) return Results.Unauthorized();
            var effectivePermissions = await authorization.GetEffectivePermissionsAsync(user.Id, ct);
            return Results.Ok(new { user, session, effectivePermissions });
        });

        group.MapPost("/logout", async (HttpContext http, IIdentityCommandV1 commands, CancellationToken ct) =>
        {
            var token = SessionToken(http);
            if (token is null || !await commands.LogoutAsync(token, Correlation(http), ct)) return Results.Unauthorized();
            return Results.NoContent();
        });
        return endpoints;
    }

    private static string? SessionToken(HttpContext http)
    {
        var authorization = http.Request.Headers.Authorization.ToString();
        if (!authorization.StartsWith("Session ", StringComparison.Ordinal)) return null;
        var token = authorization[8..].Trim();
        return token.Length is >= 32 and <= 256 ? token : null;
    }
    private static string? Correlation(HttpContext http) => http.Request.Headers["X-Correlation-ID"].FirstOrDefault() ?? System.Diagnostics.Activity.Current?.TraceId.ToString();
    public sealed record CreateUserRequest(string Username, string DisplayName, string Password);
    public sealed record LoginRequest(string Username, string Password);
}
