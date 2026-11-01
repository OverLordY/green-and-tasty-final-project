using System.Security.Claims;

namespace Restaurant.Api;

public static class OwnerOrdersEndpoints
{
    public static void MapOwnerOrderChanges(this WebApplication app)
    {
        var routes = app.MapGroup("/owner/groups/{groupId}").RequireAuthorization();
        routes.AddEndpointFilter(async (context, next) =>
        {
            context.HttpContext.Response.Headers.CacheControl = "no-store";
            return await next(context);
        });
        routes.MapPost("/orders/{orderId}/dishes", async (string groupId, string orderId, OwnerLineChange request,
            ClaimsPrincipal user, OwnerGuestOrderService service, CancellationToken ct) =>
            Results.Ok(await service.ChangeAsync(groupId, orderId, Subject(user), Email(user), request, ct)));
        routes.MapPost("/orders/{orderId}/clear", async (string groupId, string orderId, ClaimsPrincipal user,
            OwnerGuestOrderService service, CancellationToken ct) =>
            Results.Ok(await service.ClearAsync(groupId, orderId, Subject(user), Email(user), ct)));
        routes.MapPost("/orders/{orderId}/remind", async (string groupId, string orderId, ClaimsPrincipal user,
            OwnerGuestOrderService service, CancellationToken ct) =>
            Results.Ok(await service.RemindAsync(groupId, orderId, Subject(user), Email(user), ct)));
        routes.MapPost("/orders/clear", async (string groupId, ClaimsPrincipal user, OwnerGuestOrderService service, CancellationToken ct) =>
            Results.Ok(await service.ClearAllAsync(groupId, Subject(user), Email(user), ct)));
        routes.MapGet("/history", async (string groupId, ClaimsPrincipal user, OwnerGuestOrderService service, CancellationToken ct) =>
            Results.Ok(await service.HistoryAsync(groupId, Subject(user), ct)));
        routes.MapGet("/menu", async (string groupId, ClaimsPrincipal user, OwnerGuestOrderService service, CancellationToken ct) =>
            Results.Ok(await service.MenuAsync(groupId, Subject(user), ct)));
        routes.MapPost("/own-order/dishes", async (string groupId, OwnerLineChange request, ClaimsPrincipal user, OwnerGuestOrderService service, CancellationToken ct) =>
            Results.Ok(await service.ChangeOwnAsync(groupId, Subject(user), Email(user), request, ct)));
        routes.MapGet("/notifications", async (string groupId, ClaimsPrincipal user, OwnerNotificationService service, CancellationToken ct) =>
            Results.Ok(await service.StatusAsync(groupId, Subject(user), ct)));
        routes.MapPost("/notifications/{id}/send", async (string groupId, string id, ClaimsPrincipal user, OwnerNotificationService service, CancellationToken ct) =>
            Results.Ok(await service.SendAsync(groupId, id, Subject(user), ct)));
    }
    private static string Subject(ClaimsPrincipal user) => user.FindFirstValue("sub") ?? throw new ApiException(401, "Subject required.");
    private static string? Email(ClaimsPrincipal user) => string.Equals(user.FindFirstValue("email_verified"), "true", StringComparison.OrdinalIgnoreCase)
        ? user.FindFirstValue("email") : null;
}
