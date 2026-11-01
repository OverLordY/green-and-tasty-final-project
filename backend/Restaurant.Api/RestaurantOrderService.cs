namespace Restaurant.Api;

public sealed record RestaurantOrderLine(string DishId, string Name, int Quantity, decimal Total);
public sealed record RestaurantOrderReceipt(string OrderId, string ReservationId, string LocationId, int TableNumber,
    string ReservationStart, DateTimeOffset SubmittedAt, RestaurantOrderLine[] Items)
{
    public decimal Total => Items.Sum(item => item.Total);
}
public sealed record RestaurantStaffAssignment(string Subject, string LocationId);

public sealed class RestaurantOrderService(IGroupRepository groups, IConfiguration settings)
{
    public async Task<RestaurantOrderReceipt> GetAsync(string reservationId, string subject, CancellationToken ct = default)
    {
        var assignments = settings.GetSection("Restaurant:Staff").Get<RestaurantStaffAssignment[]>() ?? [];
        var locations = assignments.Where(entry => !string.IsNullOrWhiteSpace(subject) && entry.Subject == subject)
            .Select(entry => entry.LocationId).ToHashSet(StringComparer.Ordinal);
        if (locations.Count == 0) throw new ApiException(403, "Restaurant staff access is required.");
        var id = await groups.FindGroupByReservationAsync(reservationId, ct);
        var group = id is null ? null : await groups.GetGroupAsync(id, ct);
        if (group is null) throw new ApiException(404, "Reservation not found.");
        if (!locations.Contains(group.Invite.LocationId)) throw new ApiException(403, "This reservation belongs to another location.");
        if (group.Status != "Submitted" || group.RestaurantReceipt is null)
            throw new ApiException(404, "No submitted restaurant order is available.");
        return group.RestaurantReceipt;
    }
}
