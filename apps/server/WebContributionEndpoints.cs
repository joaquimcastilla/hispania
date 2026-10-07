using Hispania.ModuleSystem.Contracts;
using Hispania.Platform.Contracts.Authorization;
using Hispania.Platform.Contracts.Identity;
using Hispania.Platform.Contracts.Observability;
using Hispania.Runtime;

namespace Hispania.Server;

/// <summary>Explicit host operation catalog; descriptors never supply URLs or executable handlers.</summary>
public static class WebContributionEndpoints
{
    public static IEndpointRouteBuilder MapWebContributions(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/v1/web/contributions", async (HttpContext http, ModuleExtensionCatalog catalog,
            IIdentityQueryV1 identity, IAuthorizationServiceV1 authorization, CancellationToken ct) =>
        {
            http.Response.Headers.CacheControl = "no-store";
            var user = await User(http, identity, ct);
            if (user is null) return Results.Unauthorized();
            var snapshot = await catalog.InspectWebContributionsAsync(ct);
            var visible = new List<WebContribution>();
            foreach (var contribution in snapshot.Contributions)
                if (await authorization.HasPermissionAsync(user.Value, contribution.Page.Permission, ct)) visible.Add(contribution);
            return Results.Ok(new { schemaGeneration = WebContributionSchema.Generation, contributions = visible });
        });
        endpoints.MapGet("/api/platform/admin/modules/web", async (ModuleExtensionCatalog catalog, CancellationToken ct) =>
            Results.Ok((await catalog.InspectWebContributionsAsync(ct)).Diagnostics))
            .AddEndpointFilter(AuthorizationEndpointFilters.RequirePermission(PlatformPermissionIds.Admin, false));
        endpoints.MapGet("/api/v1/web/assets/{packageId}/{artifactHash}/{name}", async (string packageId, string artifactHash, string name, HttpContext http, ModuleExtensionCatalog catalog, CancellationToken ct) =>
        {
            http.Response.Headers.CacheControl = "no-store";
            http.Response.Headers["X-Content-Type-Options"] = "nosniff";
            var bytes = await catalog.ReadWebAssetAsync(packageId, artifactHash, name, ct);
            return bytes is null ? Results.NotFound() : Results.File(bytes, "image/png");
        });

        endpoints.MapMethods("/api/v1/web/contributions/{packageId}/{contributionId}/operations/{operation}", ["GET", "POST"],
            async (string packageId, string contributionId, string operation, HttpContext http, ModuleExtensionCatalog catalog,
                IIdentityQueryV1 identity, IAuthorizationServiceV1 authorization, IModuleRuntime runtime, IPlatformHealthV1 health, CancellationToken ct) =>
        {
            http.Response.Headers.CacheControl = "no-store";
            var user = await User(http, identity, ct);
            if (user is null) return Results.Unauthorized();
            var snapshot = await catalog.InspectWebContributionsAsync(ct);
            var contribution = snapshot.Contributions.SingleOrDefault(item => item.PackageId == packageId && item.Page.Id == contributionId);
            if (contribution is null) return Results.NotFound();
            if (!await authorization.HasPermissionAsync(user.Value, contribution.Page.Permission, ct)) return Results.StatusCode(403);
            var component = contribution.Page.Components.FirstOrDefault(component => component.Operation == operation);
            if (component is null) return Results.NotFound();
            var required = PlatformPermissionIds.ObservabilityRead;
            if (!await authorization.HasPermissionAsync(user.Value, required, ct)) return Results.StatusCode(403);
            if (operation == "platform.health" && HttpMethods.IsGet(http.Request.Method))
                return Results.Ok(new { rows = runtime.ListHealth().Take(100).Select(row => new { name = row.Descriptor.Name, value = row.IsHealthy ? "Healthy" : "Unavailable" }).ToArray() });
            if (operation == "platform.health.refresh" && HttpMethods.IsPost(http.Request.Method))
            {
                var result = await health.CheckAsync(ct);
                return Results.Ok(new { status = result.Status.ToString() });
            }
            return Results.StatusCode(405);
        });
        return endpoints;
    }

    private static async ValueTask<UserId?> User(HttpContext http, IIdentityQueryV1 identity, CancellationToken ct)
    {
        var header = http.Request.Headers.Authorization.ToString();
        var token = header.StartsWith("Session ", StringComparison.OrdinalIgnoreCase) ? header[8..].Trim() : "";
        var session = token.Length is >= 32 and <= 256 ? await identity.ValidateSessionAsync(token, ct) : null;
        return session?.UserId;
    }
}
