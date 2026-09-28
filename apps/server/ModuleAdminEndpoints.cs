using System.Security.Cryptography;
using System.Text;
using Hispania.Playback.Application;
using Hispania.Runtime;
using Hispania.ExtensionDemo.Contracts;
using Hispania.ModuleSystem.Contracts;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;

namespace Hispania.Server;

public static class ModuleAdminEndpoints
{
    private static readonly Action<ILogger, string, Version, string, Exception?> PackageInstalledLog = LoggerMessage.Define<string, Version, string>(LogLevel.Information, new EventId(1, "ExtensionPackageInstalled"), "Installed extension package {ExtensionId} version {ExtensionVersion} at {PackageDirectory}");
    private static readonly Action<ILogger, string, Exception?> PackageInstallFailedLog = LoggerMessage.Define<string>(LogLevel.Warning, new EventId(2, "ExtensionPackageInstallFailed"), "Extension package installation failed: {Error}");
    private static readonly Action<ILogger, string, string, int, Exception?> AdminOperationLog = LoggerMessage.Define<string, string, int>(LogLevel.Information, new EventId(3, "ExtensionAdminOperation"), "Module administration request {Method} {Path} completed with {StatusCode}");
    private static readonly Action<ILogger, string, Exception?> AdminAuthorizationRejectedLog = LoggerMessage.Define<string>(LogLevel.Warning, new EventId(4, "ExtensionAdminAuthorizationRejected"), "Rejected module administration request for {Path}");
    public static IEndpointRouteBuilder MapModuleAdminEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/internal/admin/modules");
        group.AddEndpointFilter(async (context, next) =>
        {
            var configuration = context.HttpContext.RequestServices.GetRequiredService<IConfiguration>();
            var logger = context.HttpContext.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("Hispania.ExtensionAdmin");
            var configuredToken = configuration["Modules:AdminToken"];
            if (string.IsNullOrWhiteSpace(configuredToken))
            {
                AdminAuthorizationRejectedLog(logger, context.HttpContext.Request.Path, null);
                return Results.Problem("Module administration is not configured.", statusCode: StatusCodes.Status503ServiceUnavailable);
            }
            var authorization = context.HttpContext.Request.Headers.Authorization.ToString();
            if (!authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            {
                AdminAuthorizationRejectedLog(logger, context.HttpContext.Request.Path, null);
                return Results.Unauthorized();
            }
            var providedToken = authorization[7..].Trim();
            var expected = Encoding.UTF8.GetBytes(configuredToken);
            var provided = Encoding.UTF8.GetBytes(providedToken);
            if (expected.Length != provided.Length || !CryptographicOperations.FixedTimeEquals(expected, provided))
            {
                AdminAuthorizationRejectedLog(logger, context.HttpContext.Request.Path, null);
                return Results.Unauthorized();
            }
            try
            {
                var result = await next(context).ConfigureAwait(false);
                AdminOperationLog(logger, context.HttpContext.Request.Method, context.HttpContext.Request.Path, context.HttpContext.Response.StatusCode, null);
                return result;
            }
            catch (Exception exception)
            {
                AdminOperationLog(logger, context.HttpContext.Request.Method, context.HttpContext.Request.Path, StatusCodes.Status500InternalServerError, exception);
                throw;
            }
        });

        group.MapGet("", (IModuleRuntime runtime, [FromServices] HotSwappablePlaybackPlanner planner, [FromServices] ModuleExtensionCatalog catalog) => Results.Ok(new
        {
            ActivePlaybackPlanner = planner.ActiveModuleId,
            Modules = runtime.ListHealth(),
            Extensions = catalog.Discover()
        }));
        group.MapGet("/remote-workers", ([FromServices] IDistributedWorkCoordinatorV1 coordinator) => Results.Ok(coordinator.ListWorkers()));
        group.MapPost("/remote-workers/{nodeId}/authorize", (string nodeId, AuthorizeWorkerRequest request, [FromServices] IDistributedWorkCoordinatorV1 coordinator) =>
        {
            try { coordinator.AuthorizeWorker(nodeId, request.AllowedCapabilities, request.CertificateThumbprint); return Results.Ok(new { nodeId, authorized = true }); }
            catch (ArgumentException exception) { return Results.BadRequest(new { error = exception.Message }); }
        });
        group.MapPut("/remote-workers/{nodeId}/certificate", (string nodeId, RotateWorkerCertificateRequest request, [FromServices] IDistributedWorkCoordinatorV1 coordinator) =>
        {
            try { coordinator.RotateWorkerCertificate(nodeId, request.CertificateThumbprint); return Results.Ok(new { nodeId, rotated = true }); }
            catch (ArgumentException exception) { return Results.BadRequest(new { error = exception.Message }); }
            catch (KeyNotFoundException) { return Results.NotFound(); }
        });
        group.MapDelete("/remote-workers/{nodeId}", (string nodeId, [FromServices] IDistributedWorkCoordinatorV1 coordinator) =>
        {
            try { coordinator.RevokeWorker(nodeId); return Results.Ok(new { nodeId, revoked = true }); }
            catch (KeyNotFoundException) { return Results.NotFound(); }
        });
        group.MapPost("/remote-workers/{nodeId}/enabled", (string nodeId, SetWorkerEnabledRequest request, [FromServices] IDistributedWorkCoordinatorV1 coordinator) =>
        {
            try { coordinator.SetWorkerEnabled(nodeId, request.Enabled); return Results.Ok(new { nodeId, request.Enabled }); }
            catch (KeyNotFoundException) { return Results.NotFound(); }
        });
        group.MapGet("/distributed-jobs", ([FromServices] IDistributedWorkCoordinatorV1 coordinator) => Results.Ok(coordinator.ListJobs()));
        group.MapGet("/extensions", ([FromServices] ModuleExtensionCatalog catalog) => Results.Ok(catalog.Discover()));
        group.MapGet("/publishers", ([FromServices] ModuleExtensionCatalog catalog, IConfiguration configuration) => Results.Ok(new
        {
            TrustedPublisherIds = catalog.ListTrustedPublisherIds(),
            RevokedPublisherIds = catalog.ListRevokedPublisherIds(),
            RequiresSignatures = configuration.GetValue("Modules:RequirePublisherSignature", false)
        }));
        group.MapPut("/publishers/{publisherId}", (string publisherId, AuthorizePublisherRequest request, [FromServices] ModuleExtensionCatalog catalog) =>
        {
            try { catalog.AuthorizePublisher(publisherId, request.PublicKeyPem); return Results.Ok(new { publisherId, authorized = true }); }
            catch (Exception exception) when (exception is ArgumentException or CryptographicException)
            { return Results.BadRequest(new { error = exception.Message }); }
        });
        group.MapDelete("/publishers/{publisherId}", async Task<IResult> (string publisherId, [FromServices] ModuleExtensionCatalog catalog, CancellationToken cancellationToken) =>
        {
            try { await catalog.RevokePublisherAsync(publisherId, cancellationToken).ConfigureAwait(false); return Results.Ok(new { publisherId, revoked = true }); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return Results.StatusCode(499); }
            catch (ModuleNotFoundException) { return Results.NotFound(); }
        });
        group.MapPost("/extensions/packages", async Task<IResult> (HttpRequest request, [FromServices] ModuleExtensionCatalog catalog, ILoggerFactory logs, CancellationToken cancellationToken) =>
        {
            try
            {
                var installed = await catalog.InstallAsync(request.Body, cancellationToken).ConfigureAwait(false);
                PackageInstalledLog(logs.CreateLogger("Hispania.ExtensionAdmin"), installed.Id, installed.Version, installed.Directory, null);
                return Results.Created($"/internal/admin/modules/extensions", installed);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return Results.StatusCode(499); }
            catch (Exception exception) when (exception is InvalidDataException or IOException or ArgumentException or System.Text.Json.JsonException)
            {
                PackageInstallFailedLog(logs.CreateLogger("Hispania.ExtensionAdmin"), exception.Message, exception);
                return Results.BadRequest(new { error = exception.Message });
            }
        }).WithMetadata(new Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute(268_435_456));
        group.MapPost("/extensions/{id}/activate", async Task<IResult> (string id, [FromServices] ModuleExtensionCatalog catalog, ILoggerFactory logs, CancellationToken cancellationToken) =>
        {
            try { return Results.Ok(await catalog.ActivateAsync(id, cancellationToken).ConfigureAwait(false)); }
            catch (ModuleNotFoundException) { return Results.NotFound(); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return Results.StatusCode(499); }
            catch (Exception exception) when (exception is InvalidOperationException or IOException or BadImageFormatException or TypeLoadException or System.Reflection.ReflectionTypeLoadException or System.Reflection.TargetInvocationException)
            {
                PackageInstallFailedLog(logs.CreateLogger("Hispania.ExtensionAdmin"), exception.Message, exception);
                return Results.Conflict(new { error = exception.Message });
            }
        });
        group.MapPost("/extensions/{id}/deactivate", async Task<IResult> (string id, [FromServices] ModuleExtensionCatalog catalog, CancellationToken cancellationToken) =>
        {
            try { return Results.Ok(await catalog.DeactivateAsync(id, cancellationToken).ConfigureAwait(false)); }
            catch (ModuleNotFoundException) { return Results.NotFound(); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return Results.StatusCode(499); }
            catch (InvalidOperationException exception) { return Results.Conflict(new { error = exception.Message }); }
        });
        group.MapPost("/extensions/{id}/unload", async Task<IResult> (string id, [FromServices] ModuleExtensionCatalog catalog, CancellationToken cancellationToken) =>
        {
            try { return Results.Ok(new { id, unloaded = await catalog.UnloadAsync(id, cancellationToken).ConfigureAwait(false) }); }
            catch (ModuleNotFoundException) { return Results.NotFound(); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return Results.StatusCode(499); }
            catch (InvalidOperationException exception) { return Results.Conflict(new { error = exception.Message }); }
        });
        group.MapPost("/extensions/{id}/update/{version}", async Task<IResult> (string id, string version, [FromServices] ModuleExtensionCatalog catalog, ILoggerFactory logs, CancellationToken cancellationToken) =>
        {
            try { return Results.Ok(await catalog.UpdateAsync(id, version, cancellationToken).ConfigureAwait(false)); }
            catch (ModuleNotFoundException) { return Results.NotFound(); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return Results.StatusCode(499); }
            catch (Exception exception) when (exception is InvalidOperationException or IOException or ArgumentException)
            {
                PackageInstallFailedLog(logs.CreateLogger("Hispania.ExtensionAdmin"), exception.Message, exception);
                return Results.Conflict(new { error = exception.Message });
            }
        });
        group.MapPost("/extensions/{id}/uninstall/{version}", async Task<IResult> (string id, string version, [FromServices] ModuleExtensionCatalog catalog, CancellationToken cancellationToken) =>
        {
            try { return Results.Ok(new { uninstalled = await catalog.UninstallAsync(id, version, cancellationToken).ConfigureAwait(false) }); }
            catch (ModuleNotFoundException) { return Results.NotFound(); }
            catch (InvalidOperationException exception) { return Results.Conflict(new { error = exception.Message }); }
        });
        group.MapGet("/extensions/{id}/greeting", (string id, [FromServices] ModuleExtensionCatalog catalog) =>
        {
            try { using var lease = catalog.Acquire<IExtensionDemoV1>(id); return Results.Ok(new { message = lease.Value.GetMessage() }); }
            catch (ModuleNotFoundException) { return Results.NotFound(); }
            catch (InvalidOperationException exception) { return Results.Conflict(new { error = exception.Message }); }
        });
        group.MapPost("/playback-planner/{targetModuleId}", async Task<IResult> (
            string targetModuleId,
            PlaybackPlannerHotSwapService swaps,
            CancellationToken cancellationToken) =>
        {
            try { return Results.Ok(await swaps.ReplaceAsync(targetModuleId, cancellationToken).ConfigureAwait(false)); }
            catch (ModuleNotFoundException) { return Results.NotFound(); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return Results.StatusCode(499); }
            catch (InvalidOperationException exception) { return Results.Conflict(new { error = exception.Message }); }
        });
        group.MapGet("/playback-experiments", ([FromServices] PlaybackExperimentService experiments) => Results.Ok(new
        { experiment = experiments.Describe(), recovery = experiments.RecoveryStatus }));
        group.MapPost("/playback-experiments", async Task<IResult> (PlaybackExperimentConfigurationRequest request, [FromServices] PlaybackExperimentService experiments, CancellationToken cancellationToken) =>
        {
            try
            {
                var snapshot = await experiments.ConfigureAsync(request, cancellationToken).ConfigureAwait(false);
                return Results.Ok(new PlaybackExperimentStatus(snapshot.ExperimentId, snapshot.Revision, snapshot.Mode.ToString(), snapshot.CapabilityId,
                    snapshot.ContractVersion.ToString(), snapshot.Weights, snapshot.ManualVariant, snapshot.ShadowVariant, snapshot.FallbackPolicy.ToString()));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return Results.StatusCode(499); }
            catch (KeyNotFoundException) { return Results.NotFound(); }
            catch (ArgumentException exception) { return Results.BadRequest(new { error = exception.Message }); }
            catch (InvalidOperationException exception) { return Results.Conflict(new { error = exception.Message }); }
        });
        group.MapPost("/playback-experiments/{experimentId}/suspend", async Task<IResult> (string experimentId, [FromServices] PlaybackExperimentService experiments, CancellationToken cancellationToken) =>
        {
            try { var snapshot = await experiments.SuspendAsync(experimentId, cancellationToken).ConfigureAwait(false); return Results.Ok(experiments.Describe()); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return Results.StatusCode(499); }
            catch (KeyNotFoundException) { return Results.NotFound(); }
            catch (InvalidOperationException exception) { return Results.Conflict(new { error = exception.Message }); }
        });
        group.MapPost("/playback-experiments/{experimentId}/variants/{moduleId}/suspend", async Task<IResult> (string experimentId, string moduleId, [FromServices] PlaybackExperimentService experiments, CancellationToken cancellationToken) =>
        {
            try { _ = await experiments.SuspendVariantAsync(experimentId, moduleId, cancellationToken).ConfigureAwait(false); return Results.Ok(experiments.Describe()); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return Results.StatusCode(499); }
            catch (KeyNotFoundException) { return Results.NotFound(); }
            catch (InvalidOperationException exception) { return Results.Conflict(new { error = exception.Message }); }
        });
        group.MapPost("/playback-experiments/{experimentId}/variants/{moduleId}/retire", async Task<IResult> (string experimentId, string moduleId, [FromServices] PlaybackExperimentService experiments, CancellationToken cancellationToken) =>
        {
            try { _ = await experiments.RetireVariantAsync(experimentId, moduleId, cancellationToken).ConfigureAwait(false); return Results.Ok(experiments.Describe()); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return Results.StatusCode(499); }
            catch (KeyNotFoundException) { return Results.NotFound(); }
            catch (InvalidOperationException exception) { return Results.Conflict(new { error = exception.Message }); }
        });
        group.MapPost("/playback-experiments/{experimentId}/rollback", async Task<IResult> (string experimentId, [FromServices] PlaybackExperimentService experiments, CancellationToken cancellationToken) =>
        {
            try { _ = await experiments.RollbackAsync(experimentId, cancellationToken).ConfigureAwait(false); return Results.Ok(experiments.Describe()); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return Results.StatusCode(499); }
            catch (KeyNotFoundException) { return Results.NotFound(); }
            catch (InvalidOperationException exception) { return Results.Conflict(new { error = exception.Message }); }
        });
        group.MapPost("/playback-experiments/{experimentId}/finish", async Task<IResult> (string experimentId, [FromServices] PlaybackExperimentService experiments, CancellationToken cancellationToken) =>
        {
            try { await experiments.FinishAsync(experimentId, cancellationToken).ConfigureAwait(false); return Results.NoContent(); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return Results.StatusCode(499); }
            catch (KeyNotFoundException) { return Results.NotFound(); }
            catch (InvalidOperationException exception) { return Results.Conflict(new { error = exception.Message }); }
        });
        group.MapGet("/playback-experiments/{experimentId}/metrics", (string experimentId, [FromServices] PlaybackExperimentService experiments) =>
        {
            try { return Results.Ok(experiments.Export(experimentId)); }
            catch (KeyNotFoundException) { return Results.NotFound(); }
            catch (InvalidOperationException exception) { return Results.Conflict(new { error = exception.Message }); }
        });
        return endpoints;
    }
}

public sealed record AuthorizePublisherRequest(string PublicKeyPem);
public sealed record AuthorizeWorkerRequest(IReadOnlyList<DistributedCapabilityV1> AllowedCapabilities, string CertificateThumbprint);
public sealed record RotateWorkerCertificateRequest(string CertificateThumbprint);
public sealed record SetWorkerEnabledRequest(bool Enabled);
