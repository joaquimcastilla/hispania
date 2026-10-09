using Hispania.Platform.Contracts.Audit;
using Hispania.Platform.Contracts.Authorization;
using Hispania.Platform.Contracts.Maintenance;
using Hispania.Platform.Maintenance.Application;
using Hispania.Platform.Persistence.Sqlite;

namespace Hispania.Server;

public static class PlatformMaintenanceEndpoints
{
    private static readonly SemaphoreSlim ReconciliationScanGate = new(1, 1);
    private static readonly Action<ILogger, Guid, string, long, long, Exception?> PlanLogged =
        LoggerMessage.Define<Guid, string, long, long>(LogLevel.Information, new EventId(2201, "RetentionPlan"),
            "Retention plan {PlanId} owner {Owner} candidates {Candidates} protected {Protected}");
    private static readonly Action<ILogger, Guid, string, int, long, MaintenanceRunStateV1, Exception?> RunLogged =
        LoggerMessage.Define<Guid, string, int, long, MaintenanceRunStateV1>(LogLevel.Information,
            new EventId(2202, "RetentionRun"),
            "Retention run {RunId} owner {Owner} batches {Batches} affected {Affected} state {State}");
    public static IEndpointRouteBuilder MapPlatformMaintenanceEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/platform/maintenance").WithTags("Platform Maintenance");
        group.MapGet("/owners", (MaintenanceCoordinatorV1 coordinator) => Results.Ok(coordinator.ListOwners()))
            .RequirePlatformPermission(PlatformPermissionIds.MaintenanceRead, allowLegacyAdminBearer: false);
        group.MapGet("/runs", async (int? limit, MaintenanceCoordinatorV1 coordinator, CancellationToken ct) =>
            Results.Ok(await coordinator.ListRunsAsync(limit ?? 50, ct)))
            .RequirePlatformPermission(PlatformPermissionIds.MaintenanceRead, allowLegacyAdminBearer: false);
        group.MapPost("/retention/plans", async (CreateRetentionPlanRequest request, HttpContext http,
            MaintenanceCoordinatorV1 coordinator, IPlatformAuditWriter audit, ILoggerFactory loggerFactory,
            CancellationToken ct) =>
        {
            if (request.MinimumAgeDays is < 0 or > 3650 || request.BatchSize is < 1 or > 500 ||
                request.MaximumRetainedCount is < 0 or > 1000000 ||
                string.IsNullOrWhiteSpace(request.OwnerId) || request.OwnerId.Length > 64)
                return Results.BadRequest(new { error = "invalid_retention_policy" });
            try
            {
                var policy = new RetentionPolicyV1(request.Enabled, TimeSpan.FromDays(request.MinimumAgeDays),
                    MaximumRetainedCount: request.MaximumRetainedCount, BatchSize: request.BatchSize);
                var plan = await coordinator.PlanAsync(request.OwnerId, policy, ct);
                await AuditAsync(audit, http, "retention.plan", request.OwnerId, "succeeded", ct);
                PlanLogged(loggerFactory.CreateLogger("Hispania.Maintenance"),
                    plan.PlanId, plan.Owner.Value, plan.CandidateCount, plan.ProtectedCount, null);
                return Results.Ok(plan);
            }
            catch (KeyNotFoundException) { return Results.NotFound(new { error = "unknown_maintenance_owner" }); }
            catch (ArgumentException) { return Results.BadRequest(new { error = "invalid_retention_policy" }); }
            catch (InvalidOperationException) { return Results.BadRequest(new { error = "invalid_retention_policy" }); }
        }).RequirePlatformPermission(PlatformPermissionIds.MaintenanceRead, allowLegacyAdminBearer: false);
        group.MapPost("/retention/plans/{planId:guid}/execute", async (Guid planId, ExecuteRetentionRequest request,
            HttpContext http, MaintenanceCoordinatorV1 coordinator, IPlatformAuditWriter audit,
            ILoggerFactory loggerFactory, CancellationToken ct) =>
        {
            if (!request.Confirm || request.MaximumBatches is < 1 or > 100)
                return Results.BadRequest(new { error = "confirmation_or_batch_limit_required" });
            var correlation = http.Request.Headers["X-Correlation-ID"].FirstOrDefault();
            if (correlation is { Length: > 128 }) return Results.BadRequest(new { error = "invalid_correlation_id" });
            try
            {
                await AuditAsync(audit, http, "retention.execute-requested", planId.ToString("D"), "attempted", ct);
                var run = await coordinator.ExecuteAsync(planId, request.MaximumBatches, correlation, ct);
                await AuditAsync(audit, http, "retention.execute-completed", run.Owner.Value, "succeeded", ct);
                RunLogged(loggerFactory.CreateLogger("Hispania.Maintenance"),
                    run.RunId, run.Owner.Value, run.Batches, run.Affected, run.State, null);
                return Results.Ok(run);
            }
            catch (KeyNotFoundException) { return Results.NotFound(new { error = "unknown_retention_plan" }); }
            catch (InvalidOperationException) { return Results.Conflict(new { error = "retention_plan_stale_or_owner_busy" }); }
        }).RequirePlatformPermission(PlatformPermissionIds.MaintenanceExecute, allowLegacyAdminBearer: false);
        group.MapGet("/capacity", async (IEnumerable<ICapacitySourceV1> sources, CancellationToken ct) =>
        {
            var snapshots = new List<CapacitySnapshotV1>();
            foreach (var source in sources) snapshots.Add(await source.ObserveAsync(ct));
            return Results.Ok(snapshots);
        }).RequirePlatformPermission(PlatformPermissionIds.CapacityRead, allowLegacyAdminBearer: false);
        group.MapGet("/reconciliation/owners", (IEnumerable<IReconciliationOwnerV1> owners) =>
            Results.Ok(owners.Select(owner => owner.OwnerId)))
            .RequirePlatformPermission(PlatformPermissionIds.MaintenanceRead, allowLegacyAdminBearer: false);
        group.MapGet("/reconciliation/{ownerId}/quarantine", async (string ownerId, int? limit,
            IEnumerable<IReconciliationOwnerV1> owners, CancellationToken ct) =>
        {
            if (limit is < 1 or > 500) return Results.BadRequest(new { error = "invalid_quarantine_limit" });
            var owner = owners.SingleOrDefault(item => item.OwnerId.Value == ownerId);
            if (owner is null) return Results.NotFound(new { error = "unknown_reconciliation_owner" });
            if (owner is not SqliteAssetReconciliationOwnerV1 assets)
                return Results.BadRequest(new { error = "quarantine_inspection_unsupported" });
            return Results.Ok(await assets.ListQuarantineAsync(limit ?? 100, ct));
        }).RequirePlatformPermission(PlatformPermissionIds.MaintenanceRead, allowLegacyAdminBearer: false);
        group.MapPost("/reconciliation/{ownerId}/scan", async (string ownerId, ReconciliationScanRequest request,
            IEnumerable<IReconciliationOwnerV1> owners, CancellationToken ct) =>
        {
            if (request.MaximumItems is < 1 or > 500 || request.ContinuationToken is { Length: > 100 })
                return Results.BadRequest(new { error = "invalid_scan_limit_or_cursor" });
            var owner = owners.SingleOrDefault(item => item.OwnerId.Value == ownerId);
            if (owner is null) return Results.NotFound(new { error = "unknown_reconciliation_owner" });
            if (!await ReconciliationScanGate.WaitAsync(0, ct))
                return Results.StatusCode(StatusCodes.Status429TooManyRequests);
            try { return Results.Ok(await owner.ScanAsync(request.MaximumItems, request.ContinuationToken, ct)); }
            catch (ArgumentException) { return Results.BadRequest(new { error = "invalid_scan_cursor" }); }
            finally { ReconciliationScanGate.Release(); }
        }).RequirePlatformPermission(PlatformPermissionIds.MaintenanceRead, allowLegacyAdminBearer: false);
        return endpoints;
    }

    private static ValueTask AuditAsync(IPlatformAuditWriter audit, HttpContext http,
        string action, string resourceId, string result, CancellationToken cancellationToken)
        => audit.WriteAsync(new AuditRecord(DateTimeOffset.UtcNow,
            AuthorizationEndpointFilters.AuthenticatedUserId(http)?.Value.ToString("D"),
            http.Request.Headers["X-Correlation-ID"].FirstOrDefault(), "platform.maintenance", action,
            "maintenance", resourceId, result, new Dictionary<string, string>()), cancellationToken);

    public sealed record CreateRetentionPlanRequest(string OwnerId, bool Enabled, int MinimumAgeDays,
        int BatchSize = 100, int? MaximumRetainedCount = null);
    public sealed record ExecuteRetentionRequest(bool Confirm, int MaximumBatches = 1);
    public sealed record ReconciliationScanRequest(int MaximumItems = 100, string? ContinuationToken = null);
}
