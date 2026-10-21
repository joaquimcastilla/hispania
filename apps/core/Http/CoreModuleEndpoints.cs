using Hispania.Core.Identity;
using Hispania.Core.Modules;
using Hispania.Runtime;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Builder;

namespace Hispania.Core.Http;

internal static class CoreModuleEndpoints
{
    private const string BasePath = "/api/v1/auth/modules";

    internal static void Map(WebApplication app)
    {
        app.MapGet(BasePath, ListAsync).WithName("CoreModulesList");
        app.MapPost(BasePath + "/install", InstallAsync).WithName("CoreModulesInstall");
        app.MapPost(BasePath + "/{id}/enable", EnableAsync).WithName("CoreModulesEnable");
        app.MapPost(BasePath + "/{id}/disable", DisableAsync).WithName("CoreModulesDisable");
        app.MapDelete(BasePath + "/{id}", RemoveAsync).WithName("CoreModulesRemove");
    }

    private static async Task<IResult> ListAsync(HttpContext context, ICoreSessionService sessions,
        ICoreIdentityService identities, ICoreAuthorizationService authorization, CoreModuleManager manager,
        CancellationToken cancellationToken)
    {
        var access = await AuthorizeAsync(context, CoreIdentityRules.ModuleReadPermission, false, sessions, identities, authorization, null, cancellationToken).ConfigureAwait(false);
        return access.Denial ?? Results.Json(manager.List());
    }

    private static async Task<IResult> InstallAsync(HttpContext context, ICoreSessionService sessions,
        ICoreIdentityService identities, ICoreAuthorizationService authorization, CoreModuleManager manager,
        IAntiforgery antiforgery, ICoreHttpAuditService audit, CancellationToken cancellationToken)
    {
        var access = await AuthorizeAsync(context, CoreIdentityRules.ModuleManagePermission, true, sessions, identities, authorization, antiforgery, cancellationToken).ConfigureAwait(false);
        if (access.Denial is not null) return access.Denial;
        if (!string.Equals(context.Request.ContentType?.Split(';')[0].Trim(), "application/zip", StringComparison.OrdinalIgnoreCase))
            return Results.BadRequest(new CoreAuthEndpoints.ErrorResponse("application_zip_required"));
        try
        {
            var id = await manager.InstallAsync(context.Request.Body, cancellationToken).ConfigureAwait(false);
            await audit.RecordAsync("http.modules.install.succeeded", access.Actor, cancellationToken).ConfigureAwait(false);
            return Results.Json(new ModuleOperationResponse(id, "installed"), statusCode: StatusCodes.Status201Created);
        }
        catch (ModuleRuntimePolicyException)
        {
            await audit.RecordAsync("http.modules.install.rejected", access.Actor, cancellationToken).ConfigureAwait(false);
            return Results.Json(new CoreAuthEndpoints.ErrorResponse("module_runtime_not_allowed"), statusCode: StatusCodes.Status400BadRequest);
        }
        catch (Exception exception) when (exception is InvalidDataException or InvalidOperationException or IOException or ArgumentException)
        {
            await audit.RecordAsync("http.modules.install.rejected", access.Actor, cancellationToken).ConfigureAwait(false);
            return Results.Json(new CoreAuthEndpoints.ErrorResponse("module_package_rejected"), statusCode: StatusCodes.Status400BadRequest);
        }
    }

    private static Task<IResult> EnableAsync(string id, HttpContext context, ICoreSessionService sessions,
        ICoreIdentityService identities, ICoreAuthorizationService authorization, CoreModuleManager manager,
        IAntiforgery antiforgery, ICoreHttpAuditService audit, CancellationToken cancellationToken) =>
        SetEnabledAsync(id, true, context, sessions, identities, authorization, manager, antiforgery, audit, cancellationToken);

    private static Task<IResult> DisableAsync(string id, HttpContext context, ICoreSessionService sessions,
        ICoreIdentityService identities, ICoreAuthorizationService authorization, CoreModuleManager manager,
        IAntiforgery antiforgery, ICoreHttpAuditService audit, CancellationToken cancellationToken) =>
        SetEnabledAsync(id, false, context, sessions, identities, authorization, manager, antiforgery, audit, cancellationToken);

    private static async Task<IResult> SetEnabledAsync(string id, bool enabled, HttpContext context, ICoreSessionService sessions,
        ICoreIdentityService identities, ICoreAuthorizationService authorization, CoreModuleManager manager,
        IAntiforgery antiforgery, ICoreHttpAuditService audit, CancellationToken cancellationToken)
    {
        var access = await AuthorizeAsync(context, CoreIdentityRules.ModuleManagePermission, true, sessions, identities, authorization, antiforgery, cancellationToken).ConfigureAwait(false);
        if (access.Denial is not null) return access.Denial;
        if (id != "hispania.slice7.fixture") return Results.NotFound();
        try
        {
            await manager.SetEnabledAsync(id, enabled, cancellationToken).ConfigureAwait(false);
            await audit.RecordAsync("http.modules.policy.changed", access.Actor, cancellationToken).ConfigureAwait(false);
            return Results.Json(new ModuleOperationResponse(id, enabled ? "enabled" : "disabled"));
        }
        catch (ModuleRuntimePolicyException)
        {
            await audit.RecordAsync("http.modules.operation.failed", access.Actor, cancellationToken).ConfigureAwait(false);
            return Results.Json(new CoreAuthEndpoints.ErrorResponse("module_runtime_not_allowed"), statusCode: StatusCodes.Status409Conflict);
        }
        catch (Exception exception) when (exception is InvalidDataException or InvalidOperationException or IOException or ArgumentException)
        {
            await audit.RecordAsync("http.modules.operation.failed", access.Actor, cancellationToken).ConfigureAwait(false);
            return Results.Json(new CoreAuthEndpoints.ErrorResponse("module_operation_failed"), statusCode: StatusCodes.Status409Conflict);
        }
    }

    private static async Task<IResult> RemoveAsync(string id, HttpContext context, ICoreSessionService sessions,
        ICoreIdentityService identities, ICoreAuthorizationService authorization, CoreModuleManager manager,
        IAntiforgery antiforgery, ICoreHttpAuditService audit, CancellationToken cancellationToken)
    {
        var access = await AuthorizeAsync(context, CoreIdentityRules.ModuleManagePermission, true, sessions, identities, authorization, antiforgery, cancellationToken).ConfigureAwait(false);
        if (access.Denial is not null) return access.Denial;
        if (id != "hispania.slice7.fixture") return Results.NotFound();
        try
        {
            await manager.RemoveAsync(id, cancellationToken).ConfigureAwait(false);
            await audit.RecordAsync("http.modules.policy.changed", access.Actor, cancellationToken).ConfigureAwait(false);
            return Results.NoContent();
        }
        catch (Exception exception) when (exception is InvalidDataException or InvalidOperationException or IOException or ArgumentException)
        {
            await audit.RecordAsync("http.modules.operation.failed", access.Actor, cancellationToken).ConfigureAwait(false);
            return Results.Json(new CoreAuthEndpoints.ErrorResponse("module_operation_failed"), statusCode: StatusCodes.Status409Conflict);
        }
    }

    private static async Task<AuthorizationCheck> AuthorizeAsync(HttpContext context, string permission, bool mutation,
        ICoreSessionService sessions, ICoreIdentityService identities, ICoreAuthorizationService authorization,
        IAntiforgery? antiforgery, CancellationToken cancellationToken)
    {
        var validation = await sessions.ValidateAsync(context.Request.Cookies[CoreAuthEndpoints.SessionCookie], DateTimeOffset.UtcNow, cancellationToken).ConfigureAwait(false);
        if (!validation.IsValid || validation.SubjectId is null) return new(CoreAuthEndpoints.Unauthorized(), null);
        var identity = await identities.FindByIdAsync(validation.SubjectId.Value, cancellationToken).ConfigureAwait(false);
        if (identity is null || identity.Status != CoreAccountStatus.Active) return new(CoreAuthEndpoints.Unauthorized(), null);
        if (!await authorization.IsAuthorizedAsync(identity.SubjectId, permission, cancellationToken).ConfigureAwait(false))
            return new(Results.StatusCode(StatusCodes.Status403Forbidden), null);
        if (!mutation) return new(null, identity.SubjectId);
        if (!CoreAuthEndpoints.HasSameOrigin(context)) return new(Results.StatusCode(StatusCodes.Status403Forbidden), null);
        try { await antiforgery!.ValidateRequestAsync(context).ConfigureAwait(false); }
        catch (AntiforgeryValidationException) { return new(Results.StatusCode(StatusCodes.Status400BadRequest), null); }
        return new(null, identity.SubjectId);
    }

    private sealed record AuthorizationCheck(IResult? Denial, Guid? Actor);
    private sealed record ModuleOperationResponse(string Id, string Status);
}
