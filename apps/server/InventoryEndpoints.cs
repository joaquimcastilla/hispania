using System.Diagnostics;
using Hispania.Inventory.Contracts;
using Hispania.Platform.Contracts.Assets;

namespace Hispania.Server;

public static class InventoryEndpoints
{
    public static IEndpointRouteBuilder MapInventoryEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/inventory").WithTags("Inventory");
        var readGroup = group.MapGroup("").AddEndpointFilter(AuthorizationEndpointFilters.RequirePermission(InventoryPermissionIds.Read, allowLegacyAdminBearer: false));
        var commandsGroup = group.MapGroup("").AddEndpointFilter(AuthorizationEndpointFilters.RequirePermission(InventoryPermissionIds.Write));
        readGroup.MapGet("/items", async (IInventoryQueryV1 query, CancellationToken token) => Results.Ok(await query.GetItemsAsync(token)));
        readGroup.MapGet("/assets/{assetId:guid}", async (Guid assetId, IPlatformAssetStore assets, CancellationToken token) =>
        {
            var metadata = await assets.GetMetadataAsync(new(assetId), token);
            return metadata is null ? Results.NotFound() : Results.Ok(new { id = metadata.Id, type = metadata.Type.ToString(), metadata.MimeType, metadata.Size, metadata.Metadata });
        });
        readGroup.MapGet("/warehouses", async (IInventoryQueryV1 query, CancellationToken token) => Results.Ok(await query.GetWarehousesAsync(token)));
        readGroup.MapGet("/locations", async (IInventoryQueryV1 query, CancellationToken token) => Results.Ok(await query.GetLocationsAsync(cancellationToken: token)));
        readGroup.MapGet("/stock", async (IInventoryQueryV1 query, CancellationToken token) => Results.Ok(await query.GetStockAsync(cancellationToken: token)));
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
        readGroup.MapGet("/items/{itemId:guid}/movements", async (Guid itemId, IInventoryQueryV1 query, CancellationToken token) => Results.Ok(await query.GetMovementsAsync(new(itemId), token)));
        return endpoints;
    }

    private static InventoryContext Context(HttpContext http)
    {
        var user = AuthorizationEndpointFilters.AuthenticatedUserId(http);
        var actor = user?.Value.ToString("D") ?? (AuthorizationEndpointFilters.IsLegacyAdmin(http) ? "legacy-admin-bearer" : null);
        return new InventoryContext(actor, http.Request.Headers["X-Correlation-ID"].FirstOrDefault() ?? Activity.Current?.TraceId.ToString());
    }
    public sealed record CreateItemRequest(string Sku, string Name);
    public sealed record CreateWarehouseRequest(string Code, string Name);
    public sealed record CreateLocationRequest(string Code, string Name);
    public sealed record StockRequest(Guid ItemId, Guid LocationId, int Quantity);
    public sealed record TransferRequest(Guid ItemId, Guid SourceLocationId, Guid DestinationLocationId, int Quantity);
    public sealed record AssetAssociationRequest(Guid AssetId);
}
