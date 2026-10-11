using Hispania.Identity.Application;
using Hispania.ModuleSystem.Contracts;
using Hispania.Platform.Contracts.Authorization;
using Hispania.Platform.Contracts.Events;
using Hispania.Platform.Contracts.Jobs;
using Hispania.Platform.Contracts.Observability;
using Hispania.Platform.Contracts.Storage;
using Hispania.Platform.Observability.Application;
using Microsoft.AspNetCore.Mvc;

namespace Hispania.Server;

public static class PlatformObservabilityEndpoints
{
    public static IServiceCollection AddPlatformObservability(this IServiceCollection services)
    {
        services.AddSingleton<IPlatformHealthV1, PlatformHealthV1>();
        services.AddSingleton<IPlatformHealthCheckV1>(sp => new DelegatePlatformHealthCheckV1("Identity", async ct =>
        {
            var active = await sp.GetRequiredService<Hispania.Platform.Persistence.Sqlite.SqliteIdentityStore>().CountActiveSessionsAsync(ct);
            sp.GetRequiredService<IPlatformMetricsV1>().BestEffort(sink => sink.Gauge("hispania.identity.sessions.active", active));
            return new("Identity", PlatformHealthStatusV1.Healthy, "store_accessible");
        }));
        services.AddSingleton<IPlatformHealthCheckV1>(sp => new DelegatePlatformHealthCheckV1("Events", async ct =>
        {
            var health = await sp.GetRequiredService<IDurablePlatformEventBusV1>().GetHealthAsync(ct);
            sp.GetRequiredService<IPlatformMetricsV1>().BestEffort(sink => { sink.Gauge("hispania.events.pending", health.Pending); sink.Gauge("hispania.events.processing", health.Processing); sink.Gauge("hispania.events.backlog.dead_lettered", health.DeadLettered); });
            return new("Events", !health.StoreAccessible ? PlatformHealthStatusV1.Unhealthy : health.DeadLettered > 0 ? PlatformHealthStatusV1.Degraded : PlatformHealthStatusV1.Healthy, !health.StoreAccessible ? "store_inaccessible" : health.DeadLettered > 0 ? "dead_letters" : "store_accessible");
        }));
        services.AddSingleton<IPlatformHealthCheckV1>(sp => new DelegatePlatformHealthCheckV1("Jobs", async ct =>
        {
            var health = await sp.GetRequiredService<IPlatformJobServiceV1>().GetHealthAsync(ct);
            return new("Jobs", !health.StoreAccessible || !health.DispatcherActive ? PlatformHealthStatusV1.Unhealthy : health.DeadLettered > 0 ? PlatformHealthStatusV1.Degraded : PlatformHealthStatusV1.Healthy,
                !health.StoreAccessible ? "store_inaccessible" : !health.DispatcherActive ? "dispatcher_inactive" : health.DeadLettered > 0 ? "dead_letters" : "store_accessible");
        }));
        services.AddSingleton<IPlatformHealthCheckV1>(sp => new DelegatePlatformHealthCheckV1("Storage", async ct =>
        {
            var resolver = sp.GetRequiredService<IStorageProviderResolverV1>();
            var health = await resolver.Resolve(resolver.DefaultProviderId).CheckHealthAsync(ct);
            return new("Storage", health.IsHealthy ? PlatformHealthStatusV1.Healthy : PlatformHealthStatusV1.Unhealthy, health.IsHealthy ? "provider_available" : "provider_unavailable");
        }));
        services.AddSingleton<IPlatformHealthCheckV1>(sp => new DelegatePlatformHealthCheckV1("Workers", async ct =>
        {
            var coordinator = sp.GetRequiredService<IDistributedWorkCoordinatorV1>();
            var workers = coordinator.ListWorkers().Where(w => w.Connected && w.Enabled && w.Authorized).ToArray();
            sp.GetRequiredService<IPlatformMetricsV1>().BestEffort(sink => { sink.Gauge("hispania.workers.connected", workers.Length); sink.Gauge("hispania.workers.capabilities", workers.SelectMany(w => w.Inventory?.Capabilities ?? []).Distinct().Count()); });
            var jobs = await sp.GetRequiredService<IPlatformJobServiceV1>().GetHealthAsync(ct);
            var waiting = !sp.GetRequiredService<IPlatformRemoteJobExecutorV1>().IsAvailable(PlatformTestHashHandlerV1.TypeId) && jobs.OldestRemoteOnlyPendingAt < DateTimeOffset.UtcNow.AddMinutes(-5);
            return new("Workers", waiting ? PlatformHealthStatusV1.Degraded : PlatformHealthStatusV1.Healthy, waiting ? "remote_workers_unavailable" : "subsystem_available");
        }));
        return services;
    }

    public static IEndpointRouteBuilder MapPlatformObservability(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/health/live", () => Results.Ok(new { status = "Healthy" }));
        endpoints.MapGet("/api/health", async (IPlatformHealthV1 health, CancellationToken ct) =>
        {
            var snapshot = await health.CheckAsync(ct);
            return Results.Json(new { status = snapshot.Status.ToString() }, statusCode: snapshot.Status == PlatformHealthStatusV1.Unhealthy ? 503 : 200);
        });
        endpoints.MapGet("/api/health/ready", async (IPlatformHealthV1 health, CancellationToken ct) =>
        {
            var snapshot = await health.CheckAsync(ct);
            return Results.Json(new { status = snapshot.Status.ToString() }, statusCode: snapshot.Status == PlatformHealthStatusV1.Unhealthy ? 503 : 200);
        });
        endpoints.MapGet("/api/health/details", async (IPlatformHealthV1 health, CancellationToken ct) => View(await health.CheckAsync(ct)))
            .RequirePlatformPermission(PlatformPermissionIds.ObservabilityRead, allowLegacyAdminBearer: false);
        endpoints.MapGet("/api/platform/observability", async (IPlatformHealthV1 health, IPlatformMetricsV1 metrics, [FromServices] IPlatformJobServiceV1 jobs,
            IDurablePlatformEventBusV1 events, [FromServices] IDistributedWorkCoordinatorV1 workers, [FromServices] IStorageProviderResolverV1 storage, CancellationToken ct) =>
        {
            var snapshot = await health.CheckAsync(ct);
            object? jobBacklog = null, eventBacklog = null;
            try { var j = await jobs.GetHealthAsync(ct); jobBacklog = new { j.Pending, j.Running, j.Failed, j.DeadLettered }; } catch (Exception) when (!ct.IsCancellationRequested) { }
            try { var e = await events.GetHealthAsync(ct); eventBacklog = new { e.Pending, e.Processing, e.Retrying, e.DeadLettered }; } catch (Exception) when (!ct.IsCancellationRequested) { }
            var executions = workers.ListJobs();
            return Results.Ok(new
            {
                health = View(snapshot), metrics = metrics.Snapshot(), jobs = jobBacklog, events = eventBacklog,
                storage = new { provider = storage.DefaultProviderId },
                workers = workers.ListWorkers().Select(worker => new { workerId = worker.NodeId, status = worker.Connected && worker.Enabled && worker.Authorized ? "Connected" : "Unavailable", lastHeartbeat = worker.LastHeartbeatUtc,
                    capabilities = worker.Inventory?.Capabilities ?? [], runningExecutions = executions.Count(job => job.NodeId == worker.NodeId && job.State is DistributedJobStateV1.Assigned or DistributedJobStateV1.Running) })
            });
        }).RequirePlatformPermission(PlatformPermissionIds.ObservabilityRead, allowLegacyAdminBearer: false);
        return endpoints;
    }
    private static object View(PlatformHealthSnapshotV1 health) => new { status = health.Status.ToString(), health.CheckedAt, checks = health.Checks.Select(check => new { check.Component, status = check.Status.ToString(), check.Code }) };
}
