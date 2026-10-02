using System.Text.Json;
using Hispania.Platform.Contracts.Authorization;
using Hispania.Platform.Contracts.Jobs;

namespace Hispania.Server;

public static class PlatformJobEndpoints
{
    public static IEndpointRouteBuilder MapPlatformJobEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/platform/jobs").WithTags("Platform Jobs");
        group.MapGet("", async (string? status, int? limit, IPlatformJobServiceV1 jobs, CancellationToken ct) =>
        {
            PlatformJobStatusV1? parsed = null;
            if (status is not null)
            {
                if (!Enum.TryParse<PlatformJobStatusV1>(status, true, out var value)) return Results.BadRequest(new { error = "invalid_status" });
                parsed = value;
            }
            return Results.Ok(await jobs.ListAsync(parsed, limit ?? 100, ct));
        }).AddEndpointFilter(AuthorizationEndpointFilters.RequirePermission(PlatformPermissionIds.JobsRead, allowLegacyAdminBearer: false));
        group.MapPost("", async (CreateJobRequest request, HttpContext http, IPlatformJobServiceV1 jobs, CancellationToken ct) =>
        {
            try
            {
                if (request.Payload.ValueKind == JsonValueKind.Undefined) return Results.BadRequest(new { error = "payload_required" });
                var payload = request.Payload.GetRawText();
                var created = await jobs.CreateAsync(new(request.JobType, payload, request.ScheduledAt, request.MaxAttempts,
                    request.TimeoutSeconds is null ? null : TimeSpan.FromSeconds(request.TimeoutSeconds.Value),
                    Correlation(http), request.CausationId, request.IdempotencyKey, AuthorizationEndpointFilters.AuthenticatedUserId(http)?.Value.ToString("D")), ct);
                var response = View(created.Job);
                return created.Duplicate ? Results.Ok(response) : Results.Accepted($"/api/platform/jobs/{created.Job.JobId:D}", response);
            }
            catch (ArgumentException ex) { return Results.BadRequest(new { error = "invalid_job", detail = ex.Message }); }
            catch (JsonException) { return Results.BadRequest(new { error = "invalid_payload" }); }
        }).AddEndpointFilter(AuthorizationEndpointFilters.RequirePermission(PlatformPermissionIds.JobsManage, allowLegacyAdminBearer: false));
        group.MapGet("/health", async (IPlatformJobServiceV1 jobs, CancellationToken ct) =>
        { try { var health = await jobs.GetHealthAsync(ct); var healthy = health.StoreAccessible && health.DispatcherActive; return Results.Json(new { status = healthy ? "Healthy" : "Degraded", health.StoreAccessible, health.DispatcherActive, health.Pending, health.Running, health.DeadLettered }, statusCode: healthy ? StatusCodes.Status200OK : StatusCodes.Status503ServiceUnavailable); } catch (Exception) { return Results.Json(new { status = "Degraded", storeAccessible = false }, statusCode: StatusCodes.Status503ServiceUnavailable); } })
            .AddEndpointFilter(AuthorizationEndpointFilters.RequirePermission(PlatformPermissionIds.JobsRead, allowLegacyAdminBearer: false));
        group.MapGet("/{jobId:guid}", async (Guid jobId, IPlatformJobServiceV1 jobs, CancellationToken ct) =>
        { var job = await jobs.GetAsync(jobId, ct); return job is null ? Results.NotFound() : Results.Ok(View(job)); })
            .AddEndpointFilter(AuthorizationEndpointFilters.RequirePermission(PlatformPermissionIds.JobsRead, allowLegacyAdminBearer: false));
        group.MapGet("/{jobId:guid}/attempts", async (Guid jobId, IPlatformJobServiceV1 jobs, CancellationToken ct) => Results.Ok(await jobs.GetAttemptsAsync(jobId, ct)))
            .AddEndpointFilter(AuthorizationEndpointFilters.RequirePermission(PlatformPermissionIds.JobsRead, allowLegacyAdminBearer: false));
        group.MapPost("/{jobId:guid}/cancel", async (Guid jobId, HttpContext http, IPlatformJobServiceV1 jobs, CancellationToken ct) =>
            await jobs.CancelAsync(jobId, AuthorizationEndpointFilters.AuthenticatedUserId(http)?.Value.ToString("D"), ct) ? Results.Accepted() : Results.Conflict(new { error = "job_not_cancellable" }))
            .AddEndpointFilter(AuthorizationEndpointFilters.RequirePermission(PlatformPermissionIds.JobsManage, allowLegacyAdminBearer: false));
        group.MapPost("/{jobId:guid}/retry", async (Guid jobId, HttpContext http, IPlatformJobServiceV1 jobs, CancellationToken ct) =>
            await jobs.RetryAsync(jobId, AuthorizationEndpointFilters.AuthenticatedUserId(http)?.Value.ToString("D"), ct) ? Results.Accepted() : Results.Conflict(new { error = "job_not_retryable" }))
            .AddEndpointFilter(AuthorizationEndpointFilters.RequirePermission(PlatformPermissionIds.JobsManage, allowLegacyAdminBearer: false));
        return endpoints;
    }

    private static object View(PlatformJobV1 job) => new
    {
        job.JobId, job.JobType, Status = job.Status.ToString(), job.CreatedAt, job.ScheduledAt, job.StartedAt,
        job.CompletedAt, job.AttemptCount, job.MaxAttempts, job.TimeoutSeconds, job.CorrelationId, job.CausationId,
        job.ResultMetadataJson, job.ErrorMetadata, job.Executor, job.LeaseExpiresAt
    };
    private static string? Correlation(HttpContext http) => http.Request.Headers["X-Correlation-ID"].FirstOrDefault() ?? System.Diagnostics.Activity.Current?.TraceId.ToString();
    public sealed record CreateJobRequest(string JobType, JsonElement Payload, DateTimeOffset? ScheduledAt = null,
        int? MaxAttempts = null, int? TimeoutSeconds = null, string? CausationId = null, string? IdempotencyKey = null);
}
