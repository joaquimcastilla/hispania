using Hispania.ModuleSystem.Contracts;
using Hispania.Platform.Contracts.Assets;
using Hispania.Platform.Contracts.Audit;
using Hispania.Platform.Contracts.Authorization;
using Hispania.Platform.Contracts.Events;
using Hispania.Platform.Contracts.Identity;
using Hispania.Platform.Contracts.Jobs;
using Hispania.Platform.Contracts.Observability;
using Hispania.Runtime;

namespace Hispania.Server;

/// <summary>Host composition of existing public services for the administrative shell.</summary>
public static class PlatformAdminEndpoints
{
    public static IEndpointRouteBuilder MapPlatformAdmin(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/platform/admin");
        group.MapGet("/overview", async (HttpContext http, IPlatformHealthV1 health, IPlatformMetricsV1 metrics,
            IIdentityQueryV1 identity, IAuthorizationServiceV1 authorization, IPlatformJobServiceV1 jobs,
            IDurablePlatformEventBusV1 events, IDistributedWorkCoordinatorV1 workers, IModuleRuntime runtime, CancellationToken ct) =>
        {
            var snapshot = await health.CheckAsync(ct);
            var userId = AuthorizationEndpointFilters.AuthenticatedUserId(http)!.Value;
            long? pending = null, deadLetters = null; int? activeUsers = null; object? modules = null;
            // Observability grants the same operational aggregates as its existing endpoint.
            try { pending = (await jobs.GetHealthAsync(ct)).Pending; } catch (Exception) when (!ct.IsCancellationRequested) { }
            try { deadLetters = (await events.GetHealthAsync(ct)).DeadLettered; } catch (Exception) when (!ct.IsCancellationRequested) { }
            if (await authorization.HasPermissionAsync(userId, PlatformPermissionIds.UsersRead, ct))
                activeUsers = (await identity.ListUsersAsync(ct)).Count(user => user.Status == UserStatus.Enabled);
            if (await authorization.HasPermissionAsync(userId, PlatformPermissionIds.Admin, ct))
            { var states = runtime.ListHealth(); modules = new { total = states.Count, healthy = states.Count(module => module.IsHealthy) }; }
            return Results.Ok(new { status = snapshot.Status.ToString(), snapshot.CheckedAt, activeUsers,
                activeSessions = metrics.Snapshot().FirstOrDefault(metric => metric.Name == "hispania.identity.sessions.active")?.Value,
                pendingJobs = pending, deadLetterEvents = deadLetters, connectedWorkers = workers.ListWorkers().Count(worker => worker.Connected && worker.Enabled && worker.Authorized),
                storageStatus = snapshot.Checks.FirstOrDefault(check => check.Component == "Storage")?.Status.ToString() ?? "Unavailable", modules });
        }).AddEndpointFilter(AuthorizationEndpointFilters.RequirePermission(PlatformPermissionIds.ObservabilityRead, false));

        group.MapGet("/assets/{id:guid}", async (Guid id, IPlatformAssetStore assets, CancellationToken ct) =>
        {
            var asset = await assets.GetMetadataAsync(new(id), ct);
            return asset is null ? Results.NotFound() : Results.Ok(new { id = asset.Id.Value, type = asset.Type.ToString(), asset.MimeType, asset.Size,
                provider = asset.ProviderId, asset.CreatedAtUtc, asset.UpdatedAtUtc });
        }).AddEndpointFilter(AuthorizationEndpointFilters.RequirePermission(PlatformPermissionIds.AssetsWrite, false));

        group.MapGet("/modules/packages/graph", (ModuleExtensionCatalog catalog) => Results.Ok(catalog.InspectPackageGraph()))
            .AddEndpointFilter(AuthorizationEndpointFilters.RequirePermission(PlatformPermissionIds.Admin, false));

        group.MapGet("/modules", (IModuleRuntime runtime, ModuleExtensionCatalog catalog) =>
        {
            var extensions = catalog.Discover().Where(extension => !extension.Manifest.Id.StartsWith("invalid:", StringComparison.Ordinal)).ToArray();
            var states = runtime.ListHealth();
            bool IsPreferred(DiscoveredExtension extension) => extensions.Where(candidate => candidate.Manifest.Id == extension.Manifest.Id)
                .OrderByDescending(candidate => Version.TryParse(candidate.Manifest.Version, out var version) ? version : new Version(0, 0)).First().Manifest.Version == extension.Manifest.Version;
            var rows = states.Select(module => new AdminModuleView(module.Descriptor.Id, module.Descriptor.Name, module.Descriptor.Version.ToString(),
                module.State.ToString(), null, module.IsHealthy,
                extensions.FirstOrDefault(extension => extension.Manifest.Id == module.Descriptor.Id && extension.Manifest.Version == module.Descriptor.Version.ToString())?.Manifest.Runtime ?? "in-process",
                extensions.Any(extension => extension.Manifest.Id == module.Descriptor.Id), true, true, false, true));
            var inactive = extensions.Where(extension => !states.Any(module => module.Descriptor.Id == extension.Manifest.Id && module.Descriptor.Version.ToString() == extension.Manifest.Version))
                .Select(extension => new AdminModuleView(extension.Manifest.Id, extension.Manifest.Name, extension.Manifest.Version,
                    extension.State?.ToString() ?? "Discovered", null, null, extension.Manifest.Runtime, true, extension.Compatible && extension.Trusted, extension.Loaded, extension.Installed, IsPreferred(extension)));
            return Results.Ok(rows.Concat(inactive));
        }).AddEndpointFilter(AuthorizationEndpointFilters.RequirePermission(PlatformPermissionIds.Admin, false));

        group.MapPost("/modules/{id}/{operation}", async Task<IResult> (string id, string operation, string? version, HttpContext http, ModuleExtensionCatalog catalog, IPlatformAuditWriter audit, CancellationToken ct) =>
        {
            if (operation is not ("activate" or "deactivate" or "unload" or "update" or "uninstall")) return Results.NotFound();
            if (operation is "update" or "uninstall" && !Version.TryParse(version, out _)) return Results.BadRequest(new { error = "invalid_version" });
            var outcome = "failure";
            try
            {
                switch (operation)
                {
                    case "activate": await catalog.ActivateAsync(id, ct); break;
                    case "update": await catalog.UpdateAsync(id, version!, ct); break;
                    case "uninstall": if (!await catalog.UninstallAsync(id, version!, ct)) return Results.NotFound(); break;
                    case "deactivate": await catalog.DeactivateAsync(id, ct); break;
                    case "unload": if (!await catalog.UnloadAsync(id, ct)) return Results.Conflict(new { error = "module_not_unloaded" }); break;
                }
                outcome = "success";
                return Results.NoContent();
            }
            catch (ModuleNotFoundException) { return Results.NotFound(); }
            catch (Exception exception) when (exception is InvalidOperationException or ArgumentException or IOException) { return Results.Conflict(new { error = "module_operation_conflict" }); }
            finally
            {
                // Unload can drain/stop before reporting a conflict: record failed attempts too.
                await audit.WriteAsync(new(DateTimeOffset.UtcNow, AuthorizationEndpointFilters.AuthenticatedUserId(http)?.Value.ToString("D"),
                    PlatformCorrelationV1.Current?.CorrelationId, "platform.modules", operation, "module", id, outcome, new Dictionary<string, string>()), ct);
            }
        }).AddEndpointFilter(AuthorizationEndpointFilters.RequirePermission(PlatformPermissionIds.Admin, false));
        return endpoints;
    }

    private sealed record AdminModuleView(string Id, string Name, string Version, string Status, long? Generation, bool? Healthy,
        string ExecutionModel, bool Extension, bool Compatible, bool Loaded, bool Installed, bool Preferred);
}
