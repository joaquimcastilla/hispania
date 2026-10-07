using System.Text.Json;
using Hispania.Platform.Contracts.Authorization;
using Hispania.Runtime;

namespace Hispania.Server;

/// <summary>Session adapters over Phase19 operations; no paths accepted from HTTP clients.</summary>
public static class PackageSetAdminEndpoints
{
    public static IEndpointRouteBuilder MapPackageSetAdmin(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/platform/admin/modules/packages")
            .AddEndpointFilter(AuthorizationEndpointFilters.RequirePermission(PlatformPermissionIds.Admin, false));
        group.MapPost("", async Task<IResult> (HttpRequest request, ModuleExtensionCatalog catalog, CancellationToken ct) =>
        {
            var path = Path.Combine(Path.GetTempPath(), "hispania-upload-" + Guid.NewGuid().ToString("N") + ".hispania");
            try
            {
                await using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
                {
                    var buffer = new byte[81920]; long total = 0; int read;
                    while ((read = await request.Body.ReadAsync(buffer, ct)) > 0)
                    { total += read; if (total > 128L * 1024 * 1024) return Results.StatusCode(413); await output.WriteAsync(buffer.AsMemory(0, read), ct); }
                }
                var candidate = await PackageArchiveInspector.InspectAsync(path, ct);
                var plan = await catalog.PlanPackageSetAsync([path], new Dictionary<string, string> { [candidate.Id] = $"[{candidate.Version}]" }, cancellationToken: ct);
                if (!plan.Resolved) return Results.Conflict(new { error = "package_preflight_rejected" });
                await catalog.ApplyPackageSetAsync(plan, cancellationToken: ct);
                return Results.Ok(new { packageId = candidate.Id, version = candidate.Version });
            }
            catch (Exception error) when (error is InvalidDataException or InvalidOperationException or IOException or ArgumentException or JsonException)
            { return Results.Conflict(new { error = "package_operation_rejected" }); }
            finally { if (File.Exists(path)) File.Delete(path); }
        }).WithMetadata(new Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute(134_217_728));
        group.MapPost("/{id}/{operation}", async Task<IResult> (string id, string operation, ModuleExtensionCatalog catalog, CancellationToken ct) =>
        {
            if (operation is not ("enable" or "disable" or "uninstall")) return Results.NotFound();
            try
            {
                var graph = catalog.InspectPackageGraph();
                if (!graph.Any(row => row.PackageId == id)) return Results.NotFound();
                await catalog.SetPackageEnabledAsync(id, operation == "enable", operation == "uninstall", ct);
                return Results.NoContent();
            }
            catch (Exception error) when (error is InvalidDataException or InvalidOperationException or IOException or ArgumentException or JsonException)
            { return Results.Conflict(new { error = "package_operation_rejected" }); }
        });
        return endpoints;
    }
}
