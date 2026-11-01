using System.Text.Json.Serialization;

namespace Restaurant.Api;

public sealed record Dish(string Id, string Name, decimal Price, string? ImageUrl = null, string? Weight = null);
public sealed record CartItem(Dish Dish, int Quantity, string? ForWhom = null, string? LineKey = null);
public sealed record Participant(string Id, string Type, string Name, bool IsSubmitted, CartItem[] Items,
    CartItem[]? PendingItems = null, string? ConfirmationStatus = null, GuestLineNotice[]? Notices = null,
    string? OwnerChangeSummary = null, string? ContactEmailMasked = null)
{
    public decimal Total => Items.Sum(item => item.Dish.Price * item.Quantity);
}
public sealed record SubmittedDish(string DishId, int Quantity);
public sealed record GroupSnapshot(Participant[] Participants, decimal Total, string? NotificationId = null, long Version = 0);
public sealed record GroupStatus(int CurrentParticipants, int MaxCapacity, string Status, bool SubmissionOverdue = false);
public sealed record OwnerSession(string GuestSessionId);
public sealed record SubmitRequest(string ReservationId, long? ExpectedVersion = null);

// PascalCase is intentional: the supplied Angular GuestInvite model uses these keys.
public sealed record GuestInvite(
    [property: JsonPropertyName("GroupReservationId")] string GroupReservationId,
    [property: JsonPropertyName("LocationId")] string LocationId,
    [property: JsonPropertyName("RestaurantName")] string RestaurantName,
    [property: JsonPropertyName("LocationAddress")] string LocationAddress,
    [property: JsonPropertyName("StartTime")] string StartTime,
    [property: JsonPropertyName("EndTime")] string EndTime,
    [property: JsonPropertyName("MaxGuestCount")] int MaxGuestCount,
    [property: JsonPropertyName("TableNumber")] int TableNumber,
    [property: JsonPropertyName("InviterName")] string InviterName);

public sealed record GroupAggregate(
    string Id, string ReservationId, string OwnerSubject, string OwnerSessionId,
    string Status, string? OrderId, string OrderStatus, string InviteToken,
    GuestInvite Invite, Participant[] Participants, SubmittedDish[] OrderItems,
    long Version = 0, Dictionary<string, StoredGuestOrder>? GuestOrders = null,
    DateTimeOffset? SubmissionDeadline = null, string? OwnerEmail = null,
    OrderAuditEntry[]? Audit = null, OrderChangeNotification[]? Notifications = null,
    RestaurantOrderReceipt? RestaurantReceipt = null, string? TableId = null, bool ReservationCancelled = false,
    int ReservationServiceStatus = 0)
{
    public GroupSnapshot Snapshot() => new(Participants.Select(person =>
    {
        if (GuestOrders is null || !GuestOrders.TryGetValue(person.Id, out var order)) return person;
        var at = order.Email.IndexOf('@');
        return person with { ContactEmailMasked = at > 0 ? order.Email[..1] + "***" + order.Email[at..] : "***" };
    }).ToArray(), Participants.Sum(person => person.Total), Version: Version);
}

public sealed class ApiException(int status, string message) : Exception(message)
{
    public int Status { get; } = status;
}

public interface IGroupRepository
{
    Task<GroupAggregate?> GetGroupAsync(string groupId, CancellationToken cancellationToken);
    Task<string?> FindGroupByReservationAsync(string reservationId, CancellationToken cancellationToken);
    Task<string?> FindGroupBySessionAsync(string sessionId, CancellationToken cancellationToken);
    Task<string?> FindGroupByInviteAsync(string token, CancellationToken cancellationToken);
    Task<bool> SaveAsync(GroupAggregate group, long expectedVersion, string? removedSessionId, CancellationToken cancellationToken);
}
