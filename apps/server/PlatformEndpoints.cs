using System.Security.Cryptography;
using System.Text;
using Hispania.Platform.Contracts.Assets;
using Hispania.Platform.Contracts.Audit;

namespace Hispania.Server;

public static class PlatformEndpoints
{
    public static IEndpointRouteBuilder MapPlatformAssetEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/assets").WithTags("Platform Assets");
        group.MapPost("", async (HttpContext http, IPlatformAssetStore assets, IPlatformAuditWriter audit, IConfiguration config, CancellationToken ct) =>
        {
            if (!await IsAdminAsync(http, config, ct)) return Results.Unauthorized();
            var maximumBytes = config.GetValue("Platform:Assets:MaximumBytes", 100L * 1024 * 1024);
            if (http.Request.ContentLength is long declaredLength && declaredLength > maximumBytes) return Results.Problem("Asset exceeds upload limit.", statusCode: StatusCodes.Status413PayloadTooLarge);
            if (!Enum.TryParse<AssetType>(http.Request.Headers["X-Asset-Type"], true, out var type)) return Results.BadRequest(new { error = "X-Asset-Type must identify an asset type." });
            var mime = http.Request.ContentType?.Split(';', 2)[0].Trim();
            if (string.IsNullOrWhiteSpace(mime) || mime.Length > 127 || mime.Contains("/../", StringComparison.Ordinal)) return Results.BadRequest(new { error = "A valid Content-Type is required." });
            if (!MatchesBasicMime(type, mime)) return Results.BadRequest(new { error = "Content-Type does not match X-Asset-Type." });
            var domain = http.Request.Headers["X-Asset-Domain"].ToString(); var owner = http.Request.Headers["X-Asset-Owner"].ToString();
            if (string.IsNullOrWhiteSpace(domain) || domain.Length > 80 || string.IsNullOrWhiteSpace(owner) || owner.Length > 128) return Results.BadRequest(new { error = "Asset ownership headers are required." });
            var isPublic = string.Equals(http.Request.Headers["X-Asset-Public"], "true", StringComparison.OrdinalIgnoreCase);
            AssetMetadata asset;
            try { asset = await assets.StoreAsync(type, mime, http.Request.Body, new(domain, owner), new(isPublic ? "public-read" : "owner-read", isPublic), cancellationToken: ct); }
            catch (InvalidDataException) { return Results.Problem("Asset exceeds upload limit.", statusCode: StatusCodes.Status413PayloadTooLarge); }
            await audit.WriteAsync(new(DateTimeOffset.UtcNow, "admin", Correlation(http), "platform.assets", "asset-created", "asset", asset.Id.Value.ToString("D"), "success", new Dictionary<string, string> { ["type"] = type.ToString(), ["scope"] = domain }), ct);
            return Results.Created($"/api/assets/{asset.Id.Value:D}", AssetView(asset));
        });
        group.MapGet("/{id:guid}", async (Guid id, HttpContext http, IPlatformAssetStore assets, IConfiguration config, CancellationToken ct) =>
        {
            var asset = await assets.GetMetadataAsync(new(id), ct); if (asset is null) return Results.NotFound();
            if (!asset.AccessPolicy.PublicRead && !await IsAdminAsync(http, config, ct)) return Results.Unauthorized();
            return Results.Ok(AssetView(asset));
        });
        group.MapGet("/{id:guid}/content", async (Guid id, HttpContext http, IPlatformAssetStore assets, IConfiguration config, CancellationToken ct) =>
        {
            var asset = await assets.GetMetadataAsync(new(id), ct); if (asset is null) return Results.NotFound();
            if (!asset.AccessPolicy.PublicRead && !await IsAdminAsync(http, config, ct)) return Results.Unauthorized();
            var stream = await assets.OpenReadAsync(new(id), ct); return stream is null ? Results.NotFound() : Results.Stream(stream, asset.MimeType, enableRangeProcessing: true);
        });
        group.MapDelete("/{id:guid}", async (Guid id, HttpContext http, IPlatformAssetStore assets, IPlatformAuditWriter audit, IConfiguration config, CancellationToken ct) =>
        {
            if (!await IsAdminAsync(http, config, ct)) return Results.Unauthorized();
            var deleted = await assets.DeleteAsync(new(id), ct);
            if (deleted) await audit.WriteAsync(new(DateTimeOffset.UtcNow, "admin", Correlation(http), "platform.assets", "asset-deleted", "asset", id.ToString("D"), "success", new Dictionary<string, string>()), ct);
            return deleted ? Results.NoContent() : Results.NotFound();
        });
        return endpoints;
    }

    public static IEndpointRouteBuilder MapPlatformAuditEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/internal/admin/audit", async (HttpContext http, IPlatformAuditReader reader, IConfiguration config, string? domain, string? action, string? resourceType, string? resourceId, string? actorId, string? correlationId, DateTimeOffset? fromUtc, DateTimeOffset? toUtc, int? page, int? pageSize, CancellationToken ct) =>
        {
            if (!await IsAdminAsync(http, config, ct)) return Results.Unauthorized();
            return Results.Ok(await reader.QueryAsync(new(domain, action, resourceType, resourceId, actorId, correlationId, fromUtc, toUtc, page ?? 1, pageSize ?? 50), ct));
        }).WithTags("Platform Audit");
        return endpoints;
    }

    /// <summary>Composition adapter advertising streamable Platform Assets to Media consumers.</summary>
    public static IEndpointRouteBuilder MapInventoryMediaAssetEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/media/assets").WithTags("Media Asset Capability");
        group.MapGet("/{id:guid}/playback", async (Guid id, IPlatformAssetStore assets, CancellationToken ct) =>
        {
            var asset = await assets.GetMetadataAsync(new(id), ct);
            if (asset is null || asset.Type != AssetType.Video || !asset.MimeType.StartsWith("video/", StringComparison.OrdinalIgnoreCase)) return Results.NotFound();
            if (!asset.AccessPolicy.PublicRead) return Results.Unauthorized();
            return Results.Ok(new { available = true, mimeType = asset.MimeType, streamUrl = $"/api/media/assets/{id:D}/stream" });
        });
        group.MapGet("/{id:guid}/stream", async (Guid id, IPlatformAssetStore assets, CancellationToken ct) =>
        {
            var asset = await assets.GetMetadataAsync(new(id), ct);
            if (asset is null || asset.Type != AssetType.Video || !asset.MimeType.StartsWith("video/", StringComparison.OrdinalIgnoreCase)) return Results.NotFound();
            if (!asset.AccessPolicy.PublicRead) return Results.Unauthorized();
            var stream = await assets.OpenReadAsync(new(id), ct);
            return stream is null ? Results.NotFound() : Results.Stream(stream, asset.MimeType, enableRangeProcessing: true);
        });
        return endpoints;
    }

    internal static ValueTask<bool> IsAdminAsync(HttpContext http, IConfiguration config, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested(); var expectedToken = config["Modules:AdminToken"];
        var authorization = http.Request.Headers.Authorization.ToString();
        if (string.IsNullOrWhiteSpace(expectedToken) || !authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) return ValueTask.FromResult(false);
        var expected = Encoding.UTF8.GetBytes(expectedToken); var provided = Encoding.UTF8.GetBytes(authorization[7..].Trim());
        return ValueTask.FromResult(expected.Length == provided.Length && CryptographicOperations.FixedTimeEquals(expected, provided));
    }
    private static string? Correlation(HttpContext http) => http.Request.Headers["X-Correlation-ID"].FirstOrDefault() ?? System.Diagnostics.Activity.Current?.TraceId.ToString();
    private static bool MatchesBasicMime(AssetType type, string mime) => type switch
    {
        AssetType.Image => mime.StartsWith("image/", StringComparison.OrdinalIgnoreCase),
        AssetType.Video => mime.StartsWith("video/", StringComparison.OrdinalIgnoreCase),
        AssetType.Audio => mime.StartsWith("audio/", StringComparison.OrdinalIgnoreCase),
        AssetType.Document => mime.Equals("application/pdf", StringComparison.OrdinalIgnoreCase) || mime.StartsWith("text/", StringComparison.OrdinalIgnoreCase),
        AssetType.Binary => mime.Equals("application/octet-stream", StringComparison.OrdinalIgnoreCase),
        _ => false
    };
    private static object AssetView(AssetMetadata asset) => new
    {
        asset.Id,
        Type = asset.Type.ToString(),
        asset.MimeType,
        asset.Size,
        asset.Metadata,
        asset.Scope,
        asset.AccessPolicy,
        Status = asset.Status.ToString(),
        asset.CreatedAtUtc,
        asset.UpdatedAtUtc
    };
}
