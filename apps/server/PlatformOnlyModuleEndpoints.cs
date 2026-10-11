using Hispania.Platform.Contracts.Authorization;
using Hispania.Runtime;
using Microsoft.AspNetCore.Mvc;

namespace Hispania.Server;

/// <summary>Platform-only module administration without the complete distribution's Playback administration.</summary>
public static class PlatformOnlyModuleEndpoints
{
    public static IEndpointRouteBuilder MapPlatformOnlyModuleEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/internal/modules/health", (IModuleRuntime runtime) => Results.Ok(runtime.ListHealth().Select(item => new
        {
            item.Descriptor,
            item.State,
            item.IsHealthy,
            item.UpdatedAtUtc,
            item.Revision
        }))).RequirePlatformPermission(PlatformPermissionIds.ObservabilityRead);

        endpoints.MapGet("/api/platform/modules", (IModuleRuntime runtime, [FromServices] ModuleExtensionCatalog packages) => Results.Ok(new
        {
            modules = runtime.ListHealth().Select(item => new
            {
                item.Descriptor.Id,
                item.Descriptor.Name,
                version = item.Descriptor.Version.ToString(),
                state = item.State.ToString(),
                item.IsHealthy
            }),
            packages = packages.Discover().Select(item => new
            {
                item.Manifest.Id,
                item.Manifest.Version,
                state = item.State.ToString()
            })
        })).RequirePlatformPermission(PlatformPermissionIds.Admin, false);

        endpoints.MapPost("/api/platform/modules/{id}/activate", async (string id, [FromServices] ModuleExtensionCatalog packages, CancellationToken ct) =>
            Results.Ok(await packages.ActivateAsync(id, ct))).RequirePlatformPermission(PlatformPermissionIds.Admin);
        endpoints.MapPost("/api/platform/modules/{id}/deactivate", async (string id, [FromServices] ModuleExtensionCatalog packages, CancellationToken ct) =>
            Results.Ok(await packages.DeactivateAsync(id, ct))).RequirePlatformPermission(PlatformPermissionIds.Admin);
        return endpoints;
    }
}
