using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Hispania.ModuleSystem.Contracts;
using Microsoft.AspNetCore.Mvc;

namespace Hispania.Server;

public static class DistributedWorkerEndpoints
{
    private const int ProtocolVersion = 1;
    private const string CertificateThumbprintItem = "hispania.worker.certificate.sha256";

    public static IEndpointRouteBuilder MapDistributedWorkerEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/internal/worker/v1");
        group.WithMetadata(new SecurityEndpointInventory.ExplicitPolicy("mtls-sha256-pin-and-node-handler"));
        group.AddEndpointFilter(async (context, next) =>
        {
            var http = context.HttpContext;
            if (!http.Request.IsHttps) return Results.StatusCode(StatusCodes.Status426UpgradeRequired);
            var nodeId = Convert.ToString(http.Request.RouteValues["nodeId"], System.Globalization.CultureInfo.InvariantCulture);
            if (string.IsNullOrWhiteSpace(nodeId)) return Results.BadRequest();
            var certificate = await http.Connection.GetClientCertificateAsync(http.RequestAborted).ConfigureAwait(false);
            if (certificate is null || certificate.NotBefore.ToUniversalTime() > DateTime.UtcNow || certificate.NotAfter.ToUniversalTime() <= DateTime.UtcNow)
                return Results.Unauthorized();
            var extendedUsage = certificate.Extensions.OfType<X509EnhancedKeyUsageExtension>().SingleOrDefault();
            if (extendedUsage is null || !extendedUsage.EnhancedKeyUsages.Cast<Oid>().Any(static oid => oid.Value == "1.3.6.1.5.5.7.3.2"))
                return Results.Unauthorized();
            var thumbprint = Convert.ToHexString(SHA256.HashData(certificate.RawData));
            var coordinator = http.RequestServices.GetRequiredService<IDistributedWorkCoordinatorV1>();
            if (!coordinator.AuthenticateWorker(nodeId, thumbprint)) return Results.Unauthorized();
            http.Items[CertificateThumbprintItem] = thumbprint;
            try { return await next(context).ConfigureAwait(false); }
            catch (ArgumentException) { return Results.BadRequest(new { error = "invalid_worker_input" }); }
        });

        group.MapPost("/{nodeId}/connect", (string nodeId, DistributedWorkerConnectRequestV1 request, HttpContext httpContext, [FromServices] IDistributedWorkCoordinatorV1 coordinator) =>
        {
            if (request.MinimumProtocolVersion < 1 || request.MaximumProtocolVersion < request.MinimumProtocolVersion ||
                request.MinimumProtocolVersion > ProtocolVersion || request.MaximumProtocolVersion < ProtocolVersion)
                return Results.Conflict(new { error = "No mutually supported distributed worker protocol version." });
            var certificateThumbprint = GetCertificateThumbprint(httpContext);
            if (certificateThumbprint is null) return Results.Unauthorized();
            return coordinator.RegisterWorker(nodeId, certificateThumbprint, request.Inventory)
                ? Results.Ok(new DistributedWorkerConnectResponseV1(ProtocolVersion, coordinator.ListWorkers().Single(worker => worker.NodeId == nodeId).AllowedCapabilities))
                : Results.Forbid();
        }).WithMetadata(new RequestSizeLimitAttribute(1_100_000));

        group.MapPost("/{nodeId}/heartbeat", (string nodeId, DistributedWorkerHeartbeatV1 heartbeat, HttpContext context, [FromServices] IDistributedWorkCoordinatorV1 coordinator) =>
        {
            if (!coordinator.Heartbeat(nodeId, heartbeat.ActiveJobIds, DateTimeOffset.UtcNow, GetLeaseDuration(context))) return Results.Unauthorized();
            var activeIds = heartbeat.ActiveJobIds.ToHashSet(StringComparer.Ordinal);
            var currentJobs = coordinator.ListJobs().Where(job => activeIds.Contains(job.Request.JobId) && job.NodeId == nodeId &&
                job.State is DistributedJobStateV1.Assigned or DistributedJobStateV1.Running && job.LeaseExpiresAtUtc > DateTimeOffset.UtcNow)
                .Select(static job => job.Request.JobId).ToHashSet(StringComparer.Ordinal);
            return Results.Ok(new DistributedWorkerHeartbeatResponseV1(activeIds.Except(currentJobs).ToArray()));
        });
        group.MapPost("/{nodeId}/disconnect", (string nodeId, HttpContext context, [FromServices] IDistributedWorkCoordinatorV1 coordinator) =>
            coordinator.DisconnectWorker(nodeId, GetCertificateThumbprint(context)) ? Results.NoContent() : Results.Unauthorized());
        group.MapPost("/{nodeId}/jobs/lease", (string nodeId, HttpContext context, [FromServices] IDistributedWorkCoordinatorV1 coordinator) =>
        {
            var lease = coordinator.LeaseNext(nodeId, DateTimeOffset.UtcNow, GetLeaseDuration(context));
            return lease is null ? Results.NoContent() : Results.Ok(lease);
        });
        group.MapPost("/{nodeId}/jobs/{jobId}/start", (string nodeId, string jobId, DistributedWorkerStartV1 request, [FromServices] IDistributedWorkCoordinatorV1 coordinator) =>
            coordinator.Start(nodeId, jobId, request.Generation) ? Results.NoContent() : Results.Conflict());
        group.MapPost("/{nodeId}/jobs/{jobId}/complete", (string nodeId, string jobId, DistributedWorkerCompletionV1 completion, [FromServices] IDistributedWorkCoordinatorV1 coordinator) =>
            coordinator.Complete(nodeId, jobId, completion.Generation, completion.Result) ? Results.NoContent() : Results.Conflict())
            .WithMetadata(new RequestSizeLimitAttribute(1_100_000));
        group.MapPost("/{nodeId}/jobs/{jobId}/fail", (string nodeId, string jobId, DistributedWorkerFailureV1 failure, [FromServices] IDistributedWorkCoordinatorV1 coordinator) =>
            coordinator.Fail(nodeId, jobId, failure.Generation, failure.FailureCode) ? Results.NoContent() : Results.Conflict());
        group.MapGet("/{nodeId}/jobs/{jobId}/media/source", async (string nodeId, string jobId, long generation, string transferId, long offset, int length,
            HttpContext context, [FromServices] IDistributedWorkCoordinatorV1 coordinator, [FromServices] IDistributedMediaTransferBrokerV1 transfers) =>
        {
            if (!HasCurrentLease(coordinator, nodeId, jobId, generation)) return Results.Conflict();
            var accessToken = GetTransferToken(context.Request);
            if (accessToken is null) return Results.Unauthorized();
            var stream = await transfers.OpenSourceBlockAsync(jobId, transferId, accessToken, offset, length, context.RequestAborted).ConfigureAwait(false);
            return stream is null ? Results.NotFound() : Results.Stream(stream, "application/octet-stream", enableRangeProcessing: false);
        });
        group.MapGet("/{nodeId}/jobs/{jobId}/media/grant", (string nodeId, string jobId, long generation, string transferId,
            IDistributedWorkCoordinatorV1 coordinator, IDistributedMediaTransferBrokerV1 transfers) =>
        {
            if (!HasCurrentLease(coordinator, nodeId, jobId, generation)) return Results.Conflict();
            var grant = transfers.GetGrant(jobId, transferId);
            return grant is null ? Results.NotFound() : Results.Ok(grant);
        });
        group.MapPut("/{nodeId}/jobs/{jobId}/media/result", async (string nodeId, string jobId, long generation, string transferId,
            long length, string sha256, HttpContext context, [FromServices] IDistributedWorkCoordinatorV1 coordinator,
            [FromServices] IDistributedMediaTransferBrokerV1 transfers) =>
        {
            if (!HasCurrentLease(coordinator, nodeId, jobId, generation)) return Results.Conflict();
            var accessToken = GetTransferToken(context.Request);
            if (accessToken is null) return Results.Unauthorized();
            var maximum = context.RequestServices.GetRequiredService<IConfiguration>().GetValue("DistributedWorkers:MaximumResultBytes", 20L * 1024 * 1024 * 1024);
            if (length <= 0 || length > maximum || (context.Request.ContentLength is { } contentLength && contentLength != length)) return Results.BadRequest();
            var sizeFeature = context.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>();
            if (sizeFeature is { IsReadOnly: false }) sizeFeature.MaxRequestBodySize = Math.Min(maximum, 100L * 1024 * 1024 * 1024);
            var handle = await transfers.ReceiveResultAsync(jobId, generation, transferId, accessToken, length, sha256,
                context.Request.Body, () => HasCurrentLease(coordinator, nodeId, jobId, generation), context.RequestAborted).ConfigureAwait(false);
            if (handle is null) return Results.BadRequest(new { error = "Media result failed authorization, size, or integrity validation." });
            if (coordinator.Complete(nodeId, jobId, generation, JsonSerializer.SerializeToElement(new { handle, length, sha256 })))
                return Results.Ok(new { handle, length, sha256 });
            // Publication and coordinator state use separate durable systems. If the lease was
            // fenced in the narrow interval after the broker's final lease check, discard the
            // uncommitted artifact so no incompatible result remains addressable.
            transfers.RemovePublishedResult(handle);
            return Results.Conflict();
        });
        return endpoints;
    }

    private static bool HasCurrentLease(IDistributedWorkCoordinatorV1 coordinator, string nodeId, string jobId, long generation) =>
        coordinator.ListJobs().Any(job => job.Request.JobId == jobId && job.NodeId == nodeId && job.Generation == generation &&
            job.State == DistributedJobStateV1.Running && job.LeaseExpiresAtUtc > DateTimeOffset.UtcNow);

    private static string? GetTransferToken(HttpRequest request)
    {
        var value = request.Headers.Authorization.ToString();
        return value.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) && value.Length > 7 ? value[7..] : null;
    }

    private static TimeSpan GetLeaseDuration(HttpContext context)
    {
        var seconds = context.RequestServices.GetRequiredService<IConfiguration>().GetValue("DistributedWorkers:LeaseSeconds", 30);
        return TimeSpan.FromSeconds(Math.Clamp(seconds, 5, 300));
    }

    private static string GetCertificateThumbprint(HttpContext context) => context.Items[CertificateThumbprintItem] as string
        ?? throw new InvalidOperationException("Worker certificate was not authenticated.");

}
