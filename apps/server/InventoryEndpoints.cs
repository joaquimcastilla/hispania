using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Hispania.Inventory.Contracts;
using Hispania.Platform.Contracts.Assets;
using Hispania.Platform.Contracts.Audit;

namespace Hispania.Server;

public static class InventoryEndpoints
{
    public static IEndpointRouteBuilder MapInventoryEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/inventory").WithTags("Inventory");
        var commandsGroup = group.MapGroup("").AddEndpointFilter(RequireAdminTokenAsync);
        group.MapGet("/items", async (IInventoryQueryV1 query, CancellationToken token) => Results.Ok(await query.GetItemsAsync(token)));
        group.MapGet("/assets/{assetId:guid}", async (Guid assetId, HttpContext http, IConfiguration configuration, IPlatformAssetStore assets, CancellationToken token) =>
        {
            var metadata = await assets.GetMetadataAsync(new(assetId), token);
            if (metadata is not null && !metadata.AccessPolicy.PublicRead && !await PlatformEndpoints.IsAdminAsync(http, configuration, token)) return Results.Unauthorized();
            return metadata is null ? Results.NotFound() : Results.Ok(new { id = metadata.Id, type = metadata.Type.ToString(), metadata.MimeType, metadata.Size, metadata.Metadata });
        });
        group.MapGet("/warehouses", async (IInventoryQueryV1 query, CancellationToken token) => Results.Ok(await query.GetWarehousesAsync(token)));
        group.MapGet("/locations", async (IInventoryQueryV1 query, CancellationToken token) => Results.Ok(await query.GetLocationsAsync(cancellationToken: token)));
        group.MapGet("/stock", async (IInventoryQueryV1 query, CancellationToken token) => Results.Ok(await query.GetStockAsync(cancellationToken: token)));
        commandsGroup.MapPost("/items", async (CreateItemRequest request, IInventoryCommandV1 commands, HttpContext http, CancellationToken token) =>
        {
            var item = await commands.CreateItemAsync(request.Sku, request.Name, Context(http), token);
            return Results.Created($"/api/inventory/items/{item.Id.Value:D}", item);
        });
        commandsGroup.MapPost("/warehouses", async (CreateWarehouseRequest request, IInventoryCommandV1 commands, HttpContext http, CancellationToken token) => Results.Ok(await commands.CreateWarehouseAsync(request.Code, request.Name, Context(http), token)));
        commandsGroup.MapPost("/warehouses/{warehouseId:guid}/locations", async (Guid warehouseId, CreateLocationRequest request, IInventoryCommandV1 commands, HttpContext http, CancellationToken token) => Results.Ok(await commands.CreateLocationAsync(new(warehouseId), request.Code, request.Name, Context(http), token)));
        commandsGroup.MapPost("/receive", async (StockRequest request, IInventoryCommandV1 commands, HttpContext http, CancellationToken token) => Results.Ok(await commands.ReceiveAsync(new(request.ItemId), new(request.LocationId), request.Quantity, Context(http), token)));
        commandsGroup.MapPost("/items/{itemId:guid}/assets", async (Guid itemId, AssetAssociationRequest request, IInventoryCommandV1 commands, HttpContext http, CancellationToken token) =>
        {
            await commands.AssociateAssetAsync(new(itemId), new(new(request.AssetId)), Context(http), token);
            return Results.NoContent();
        });
        commandsGroup.MapDelete("/items/{itemId:guid}/assets/{assetId:guid}", async (Guid itemId, Guid assetId, IInventoryCommandV1 commands, HttpContext http, CancellationToken token) =>
        {
            await commands.RemoveAssetAsync(new(itemId), new(assetId), Context(http), token);
            return Results.NoContent();
        });
        commandsGroup.MapPost("/issue", async (StockRequest request, IInventoryCommandV1 commands, HttpContext http, CancellationToken token) => Results.Ok(await commands.IssueAsync(new(request.ItemId), new(request.LocationId), request.Quantity, Context(http), token)));
        commandsGroup.MapPost("/transfer", async (TransferRequest request, IInventoryCommandV1 commands, HttpContext http, CancellationToken token) => Results.Ok(await commands.TransferAsync(new(request.ItemId), new(request.SourceLocationId), new(request.DestinationLocationId), request.Quantity, Context(http), token)));
        group.MapGet("/items/{itemId:guid}/movements", async (Guid itemId, IInventoryQueryV1 query, CancellationToken token) => Results.Ok(await query.GetMovementsAsync(new(itemId), token)));
        return endpoints;
    }

    private static InventoryContext Context(HttpContext http) => new(http.User.Identity?.IsAuthenticated == true ? http.User.Identity.Name : null, http.Request.Headers["X-Correlation-ID"].FirstOrDefault() ?? Activity.Current?.TraceId.ToString());
    private static async ValueTask<object?> RequireAdminTokenAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var configuration = context.HttpContext.RequestServices.GetRequiredService<IConfiguration>();
        var expectedToken = configuration["Modules:AdminToken"];
        if (string.IsNullOrWhiteSpace(expectedToken)) return Results.Problem("Inventory write access is not configured.", statusCode: StatusCodes.Status503ServiceUnavailable);
        var authorization = context.HttpContext.Request.Headers.Authorization.ToString();
        if (!authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) return Results.Unauthorized();
        var expected = Encoding.UTF8.GetBytes(expectedToken);
        var provided = Encoding.UTF8.GetBytes(authorization[7..].Trim());
        if (expected.Length != provided.Length || !CryptographicOperations.FixedTimeEquals(expected, provided)) return Results.Unauthorized();
        return await next(context);
    }
    public sealed record CreateItemRequest(string Sku, string Name);
    public sealed record CreateWarehouseRequest(string Code, string Name);
    public sealed record CreateLocationRequest(string Code, string Name);
    public sealed record StockRequest(Guid ItemId, Guid LocationId, int Quantity);
    public sealed record TransferRequest(Guid ItemId, Guid SourceLocationId, Guid DestinationLocationId, int Quantity);
    public sealed record AssetAssociationRequest(Guid AssetId);
}
