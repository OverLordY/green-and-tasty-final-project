using System.Globalization;

namespace Restaurant.Api;

public sealed record CreateGuestOrderRequest(string InviteToken, string GuestName, string Email);
public sealed record ReduceGuestOrderRequest(string LineKey, int Quantity);
public sealed record ConfirmGuestOrderRequest(string Token);
public sealed record ConfirmationPreview(GuestOrderLine[] Confirmed, GuestOrderLine[] Pending);
public sealed record ProposeGuestDishRequest(string DishId, string ForWhom, int Quantity);
public sealed record MenuEntry(string Id, string Name, decimal Price, bool Available,
    string Category = "Appetizers", string? ImageUrl = null, string? Weight = null, string? LocationId = null);
public static class RestaurantMenu
{
    public static MenuEntry[] ForLocation(IConfiguration settings, string locationId)
        => (settings.GetSection("Restaurant:Menu").Get<MenuEntry[]>() ?? [])
            .Where(item => string.IsNullOrEmpty(item.LocationId) || item.LocationId == locationId).ToArray();
}
public sealed record GuestMenuRequest(string InviteToken);
public sealed record GuestMenuPreview(GuestInvite Invite, MenuEntry[] Items, OrderDeadlines Deadlines, bool CanChange);
public sealed record GuestPersonalMenu(MenuEntry[] Items, DateTimeOffset? ChangeDeadline, bool CanChange);
public sealed record GuestAccessEmailRequest(string Email);
public sealed record GuestInviteAccessRequest(string InviteToken, string Email);

public static class GuestOrdersEndpoints
{
    public static void MapGuestOrders(this WebApplication app)
    {
        var routes = app.MapGroup("/guest/orders").AllowAnonymous().RequireRateLimiting("guest-orders");
        routes.AddEndpointFilter(async (context, next) =>
        {
            var response = context.HttpContext.Response;
            response.Headers.CacheControl = "no-store";
            response.Headers.Pragma = "no-cache";
            response.Headers["Referrer-Policy"] = "no-referrer";
            try { return await next(context); }
            catch (GuestAccessLinkException exception) { return Results.Json(new { code = exception.Code, error = "Recovery link is invalid, expired or already used." }, statusCode: exception.Status); }
            catch (GuestMailUnavailableException) { return Results.Json(new { error = "Email delivery is unavailable. Please try again later." }, statusCode: 503); }
            catch (UnauthorizedAccessException) { return Results.Json(new { error = "Access denied or confirmation link expired." }, statusCode: 401); }
            catch (KeyNotFoundException) { return Results.Json(new { error = "Order or dish not found." }, statusCode: 404); }
            catch (ArgumentException) { return Results.BadRequest(new { error = "Invalid order data." }); }
            catch (InvalidOperationException exception)
            {
                return Results.Json(new { error = exception.Message }, statusCode: 409);
            }
        });

        // Identical public response for unknown email, wrong invitation, cooldown and provider failure.
        static IResult AccessRequested() => Results.Json(new { message = "Якщо замовлення пов’язане з цією поштою, надішлемо лист для відновлення доступу. Повторний запит доступний через хвилину." }, statusCode: 202);
        routes.MapPost("/recover-access", async (GuestInviteAccessRequest request, GuestAccessRecoveryService service, CancellationToken ct) =>
        {
            await service.RequestForInviteAsync(request.InviteToken, request.Email, ct);
            return AccessRequested();
        });
        routes.MapPost("/{orderId}/request-access", async (string orderId, GuestAccessEmailRequest request, GuestAccessRecoveryService service, CancellationToken ct) =>
        {
            await service.RequestForOrderAsync(orderId, request.Email, ct);
            return AccessRequested();
        });
        routes.MapGet("/{orderId}/recover-access", () => Results.Ok(new { requiresExplicitAction = true }));
        routes.MapPost("/{orderId}/recover-access", async (string orderId, ConfirmGuestOrderRequest request, GuestAccessRecoveryService service, CancellationToken ct) =>
            Results.Ok(await service.RedeemAsync(orderId, request.Token, ct)));
        routes.MapGet("/{orderId}/menu", async (string orderId, HttpContext context, GuestOrderService service, IConfiguration settings, IGroupRepository groups, CancellationToken ct) =>
        {
            var view = await service.GetAsync(orderId, Access(context), ct);
            var groupId = await groups.FindGroupBySessionAsync(orderId, ct) ?? throw new UnauthorizedAccessException();
            var group = await groups.GetGroupAsync(groupId, ct) ?? throw new UnauthorizedAccessException();
            return Results.Ok(new GuestPersonalMenu(RestaurantMenu.ForLocation(settings, group.Invite.LocationId), view.ChangeDeadline, view.CanChange == true));
        });

        routes.MapPost("/preview", async (GuestMenuRequest request, IGroupRepository groups,
            IConfiguration settings, TimeProvider clock, CancellationToken cancellationToken) =>
        {
            var group = await ResolveInviteAsync(request.InviteToken, groups, cancellationToken);
            var deadlines = Deadlines(group, settings);
            var menu = RestaurantMenu.ForLocation(settings, group.Invite.LocationId);
            return Results.Ok(new GuestMenuPreview(group.Invite, menu, deadlines,
                group.Status == "Open" && deadlines.CanChange(clock.GetUtcNow())));
        });
        routes.MapPost("/", async (CreateGuestOrderRequest request, IGroupRepository groups,
            GuestOrderService service, IConfiguration settings, TimeProvider clock, CancellationToken cancellationToken) =>
        {
            var group = await ResolveInviteAsync(request.InviteToken, groups, cancellationToken);
            if (group.Status != "Open") throw new InvalidOperationException("The group is not open.");
            var deadlines = Deadlines(group, settings);
            var draft = await service.CreateDraftAsync(group.Id, request.GuestName, request.Email,
                deadlines.ChangesClose, clock.GetUtcNow(), TimeSpan.FromMinutes(15), cancellationToken);
            return Results.Json(draft, statusCode: 201);
        });
        routes.MapGet("/{orderId}", async (string orderId, HttpContext context, GuestOrderService service,
            CancellationToken cancellationToken) =>
            Results.Ok(await service.GetAsync(orderId, Access(context), cancellationToken)));
        routes.MapPost("/{orderId}/dishes", async (string orderId, ProposeGuestDishRequest request,
            HttpContext context, GuestOrderService service, IConfiguration settings, IGroupRepository groups, TimeProvider clock,
            CancellationToken cancellationToken) =>
        {
            var access = Access(context);
            await service.GetAsync(orderId, access, cancellationToken);
            if (string.IsNullOrWhiteSpace(request.DishId) || string.IsNullOrWhiteSpace(request.ForWhom) || request.ForWhom.Length > 100)
                throw new ArgumentException("Dish and recipient are required.");
            var groupId = await groups.FindGroupBySessionAsync(orderId, cancellationToken) ?? throw new UnauthorizedAccessException();
            var group = await groups.GetGroupAsync(groupId, cancellationToken) ?? throw new UnauthorizedAccessException();
            var menu = RestaurantMenu.ForLocation(settings, group.Invite.LocationId);
            var dish = menu.SingleOrDefault(item => item.Id == request.DishId) ?? throw new KeyNotFoundException();
            if (!dish.Available) throw new InvalidOperationException("Dish is unavailable.");
            var recipient = request.ForWhom.Trim();
            var key = dish.Id + ":" + recipient;
            return Results.Ok(await service.ProposeAsync(orderId, access,
                new(key, dish.Id, dish.Name, recipient, dish.Price, request.Quantity), clock.GetUtcNow(), cancellationToken));
        });
        routes.MapPost("/{orderId}/reduce", async (string orderId, ReduceGuestOrderRequest request,
            HttpContext context, GuestOrderService service, TimeProvider clock, CancellationToken cancellationToken) =>
            Results.Ok(await service.ReduceAsync(orderId, Access(context), request.LineKey, request.Quantity, clock.GetUtcNow(), cancellationToken)));
        routes.MapPost("/{orderId}/request-confirmation", async (string orderId, HttpContext context,
            GuestOrderService service, TimeProvider clock, CancellationToken cancellationToken) =>
        {
            await service.RequestConfirmationAsync(orderId, Access(context), clock.GetUtcNow(), cancellationToken);
            return Results.Accepted();
        });
        routes.MapPost("/{orderId}/confirm", async (string orderId, ConfirmGuestOrderRequest request,
            GuestOrderService service, TimeProvider clock, CancellationToken cancellationToken) =>
            Results.Ok(await service.ConfirmAsync(orderId, request.Token, clock.GetUtcNow(), cancellationToken)));
        routes.MapPost("/{orderId}/confirmation-preview", async (string orderId, ConfirmGuestOrderRequest request,
            GuestOrderService service, TimeProvider clock, CancellationToken cancellationToken) =>
            Results.Ok(await service.PreviewAsync(orderId, request.Token, clock.GetUtcNow(), cancellationToken)));
        // GET never confirms an order; mail scanners and browser previews cannot cause confirmation.
        routes.MapGet("/{orderId}/confirm", () => Results.Ok(new { requiresExplicitConfirmation = true }));
    }

    private static string Access(HttpContext context)
    {
        var header = context.Request.Headers.Authorization.ToString();
        if (!header.StartsWith("Bearer ", StringComparison.Ordinal) || header.Length <= 7)
            throw new UnauthorizedAccessException();
        return header[7..];
    }

    private static async Task<GroupAggregate> ResolveInviteAsync(string token, IGroupRepository groups, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(token)) throw new UnauthorizedAccessException();
        var id = await groups.FindGroupByInviteAsync(token, cancellationToken) ?? throw new UnauthorizedAccessException();
        return await groups.GetGroupAsync(id, cancellationToken) ?? throw new UnauthorizedAccessException();
    }
    private static OrderDeadlines Deadlines(GroupAggregate group, IConfiguration settings)
    {
        if (!DateTimeOffset.TryParse(group.Invite.StartTime, CultureInfo.InvariantCulture, DateTimeStyles.None, out var start))
            throw new InvalidOperationException("Reservation start time is invalid.");
        return OrderDeadlines.FromReservation(start,
            TimeSpan.FromMinutes(settings.GetValue<int?>("Restaurant:PreparationMinutes") ?? 30),
            TimeSpan.FromMinutes(settings.GetValue<int?>("Restaurant:ReviewMinutes") ?? 15));
    }
}
