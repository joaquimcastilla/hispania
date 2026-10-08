using Hispania.Platform.Contracts.Authorization;
using Hispania.Platform.Contracts.Events;
using Hispania.Platform.Contracts.Audit;

namespace Hispania.Server;

public static class PlatformEventAdminEndpoints
{
    public static IEndpointRouteBuilder MapPlatformEventAdminEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/internal/admin/events").WithTags("Platform Events");
        group.MapGet("/deliveries", async (string? status, int? limit, IDurablePlatformEventBusV1 bus, CancellationToken ct) =>
        {
            DurableDeliveryStatus? parsed = null;
            if (status is not null && !Enum.TryParse<DurableDeliveryStatus>(status, true, out var value)) return Results.BadRequest();
            else if (status is not null) parsed = Enum.Parse<DurableDeliveryStatus>(status, true);
            var items = await bus.ListDeliveriesAsync(parsed, limit ?? 100, ct);
            return Results.Ok(items.Select(x => new { x.DeliveryId, x.EventId, x.EventType, x.SubscriberId, Status = x.Status.ToString(), x.AttemptCount, x.NextAttemptAt, x.LastError, x.DeliveredAt, x.DeadLetteredAt }));
        }).RequirePlatformPermission(PlatformPermissionIds.EventsRead, allowLegacyAdminBearer: false);
        group.MapGet("/health", async (IDurablePlatformEventBusV1 bus, CancellationToken ct) =>
        {
            var health = await bus.GetHealthAsync(ct);
            return Results.Ok(new { status = health.DeadLettered > 0 ? "Degraded" : "Healthy", health.Pending, health.Processing, health.Delivered, health.DeadLettered, health.StoreAccessible });
        }).RequirePlatformPermission(PlatformPermissionIds.EventsRead, allowLegacyAdminBearer: false);
        group.MapGet("/{eventId:guid}", async (Guid eventId, IDurablePlatformEventBusV1 bus, CancellationToken ct) =>
        { var result = await bus.GetEventAsync(eventId, ct); return result is null ? Results.NotFound() : Results.Ok(result); })
            .RequirePlatformPermission(PlatformPermissionIds.EventsManage, allowLegacyAdminBearer: false);
        group.MapGet("/deliveries/{deliveryId:guid}/attempts", async (Guid deliveryId, IDurablePlatformEventBusV1 bus, CancellationToken ct) => Results.Ok(await bus.GetDeliveryAttemptsAsync(deliveryId, ct)))
            .RequirePlatformPermission(PlatformPermissionIds.EventsManage, allowLegacyAdminBearer: false);
        group.MapPost("/deliveries/{deliveryId:guid}/replay", async (Guid deliveryId, HttpContext http, IDurablePlatformEventBusV1 bus, IPlatformAuditWriter audit, CancellationToken ct) =>
        {
            if (!await bus.ReplayDeadLetterAsync(deliveryId, ct)) return Results.Conflict();
            await audit.WriteAsync(new(DateTimeOffset.UtcNow, AuthorizationEndpointFilters.AuthenticatedUserId(http)?.Value.ToString("D"), http.Request.Headers["X-Correlation-ID"].FirstOrDefault(), "platform.events", "delivery.replay", "delivery", deliveryId.ToString("D"), "succeeded", new Dictionary<string, string>()), ct);
            return Results.Accepted();
        })
            .RequirePlatformPermission(PlatformPermissionIds.EventsManage, allowLegacyAdminBearer: false);
        return endpoints;
    }
}