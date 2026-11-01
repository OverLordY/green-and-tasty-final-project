namespace Restaurant.Api;

public sealed class GroupService(IGroupRepository repository, TimeProvider? clock = null)
{
    private async Task<GroupAggregate> GetOwnedAsync(string groupId, string subject, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(subject)) throw new ApiException(401, "Authentication is required.");
        var group = await repository.GetGroupAsync(groupId, cancellationToken)
            ?? throw new ApiException(404, "Group reservation not found");
        if (!string.Equals(group.OwnerSubject, subject, StringComparison.Ordinal))
            throw new ApiException(403, "Only the reservation owner can perform this operation.");
        return group;
    }

    public async Task<GroupSnapshot> GetSessionsAsync(string groupId, string subject, CancellationToken cancellationToken = default)
        => (await GetOwnedAsync(groupId, subject, cancellationToken)).Snapshot();

    public async Task<OwnerSession> GetOwnerSessionAsync(string reservationId, string subject, CancellationToken cancellationToken = default)
    {
        var groupId = await repository.FindGroupByReservationAsync(reservationId, cancellationToken)
            ?? throw new ApiException(404, "Reservation not found.");
        var group = await GetOwnedAsync(groupId, subject, cancellationToken);
        if (!group.Participants.Any(person => person.Id == group.OwnerSessionId && person.Type == "Owner"))
            throw new ApiException(404, "Owner session not found.");
        return new OwnerSession(group.OwnerSessionId);
    }

    public async Task<SubmittedDish[]> SubmitAsync(string groupId, string reservationId, string subject, CancellationToken cancellationToken = default, long? expectedVersion = null)
    {
        if (string.IsNullOrWhiteSpace(reservationId)) throw new ApiException(400, "reservationId is required.");
        var group = await GetOwnedAsync(groupId, subject, cancellationToken);
        if (expectedVersion is not null && expectedVersion != group.Version)
            throw new ApiException(409, "The order changed. Refresh and review before submitting.");
        var reservationGroupId = await repository.FindGroupByReservationAsync(reservationId, cancellationToken);
        if (reservationGroupId is null) throw new ApiException(404, "Reservation not found.");
        if (reservationGroupId != group.Id || group.ReservationId != reservationId)
            throw new ApiException(400, "Reservation does not belong to this group.");
        if (group.Status != "Open") throw new ApiException(400, "Group reservation is not open for submission.");
        if (group.SubmissionDeadline is { } deadline && (clock?.GetUtcNow() ?? DateTimeOffset.UtcNow) >= deadline)
            throw new ApiException(410, "Замовлення не надіслано вчасно. Для уточнення зверніться до ресторану.");
        if (string.IsNullOrWhiteSpace(group.OrderId)) throw new ApiException(400, "Active order not found for this reservation.");
        var sessions = group.Participants.Where(person => person.IsSubmitted).ToArray();
        if (sessions.Length == 0) throw new ApiException(400, "No submitted guest sessions found to consolidate.");
        var items = sessions.SelectMany(person => person.Items).ToArray();
        if (items.Length == 0) throw new ApiException(400, "No dishes selected in the submitted guest sessions.");
        if (items.Any(item => item.Quantity <= 0 || item.Dish.Price < 0 || string.IsNullOrWhiteSpace(item.Dish.Id)))
            throw new ApiException(400, "Submitted carts contain invalid items.");
        SubmittedDish[] consolidated;
        try
        {
            consolidated = items.GroupBy(item => item.Dish.Id, StringComparer.Ordinal)
                .Select(grouping => new SubmittedDish(grouping.Key, grouping.Sum(item => item.Quantity))).ToArray();
        }
        catch (OverflowException)
        {
            throw new ApiException(400, "Dish quantity exceeds supported limits.");
        }
        var updated = group with
        {
            Status = "Submitted", OrderStatus = "SubmittedByCustomer",
            OrderItems = consolidated, Version = checked(group.Version + 1),
            RestaurantReceipt = new(group.OrderId!, group.ReservationId, group.Invite.LocationId,
                group.Invite.TableNumber, group.Invite.StartTime, clock?.GetUtcNow() ?? DateTimeOffset.UtcNow,
                items.GroupBy(item => item.Dish.Id, StringComparer.Ordinal).Select(lines =>
                    new RestaurantOrderLine(lines.Key, lines.First().Dish.Name, lines.Sum(line => line.Quantity),
                        lines.Sum(line => line.Quantity * line.Dish.Price))).ToArray())
        };
        if (!await repository.SaveAsync(updated, group.Version, null, cancellationToken))
            throw new ApiException(409, "The group changed. Refresh and try again.");
        return consolidated;
    }

    public async Task<GroupSnapshot> RemoveAsync(string groupId, string sessionId, string subject, CancellationToken cancellationToken = default)
    {
        var group = await GetOwnedAsync(groupId, subject, cancellationToken);
        var sessionGroupId = await repository.FindGroupBySessionAsync(sessionId, cancellationToken);
        if (sessionGroupId is null) throw new ApiException(404, "Guest session not found.");
        if (sessionGroupId != groupId) throw new ApiException(400, "Guest session does not belong to this group reservation.");
        var participant = group.Participants.SingleOrDefault(person => person.Id == sessionId)
            ?? throw new ApiException(404, "Guest session not found.");
        if (participant.Type == "Owner") throw new ApiException(400, "Owner session cannot be removed.");
        if (group.Status != "Open") throw new ApiException(400, "Guests cannot be removed after the group is closed or submitted.");
        var updated = group with
        {
            Participants = group.Participants.Where(person => person.Id != sessionId).ToArray(),
            Version = checked(group.Version + 1)
        };
        if (!await repository.SaveAsync(updated, group.Version, sessionId, cancellationToken))
            throw new ApiException(409, "The group changed. Refresh and try again.");
        return updated.Snapshot();
    }

    public async Task<GroupStatus> GetStatusAsync(string groupId, string subject, CancellationToken cancellationToken = default)
    {
        var group = await GetOwnedAsync(groupId, subject, cancellationToken);
        return new(group.Participants.Length, group.Invite.MaxGuestCount, group.Status,
            group.Status == "Open" && group.SubmissionDeadline is { } deadline && (clock?.GetUtcNow() ?? DateTimeOffset.UtcNow) >= deadline);
    }

    public async Task<string> GetInviteTokenAsync(string reservationId, string subject, CancellationToken cancellationToken = default)
    {
        var groupId = await repository.FindGroupByReservationAsync(reservationId, cancellationToken)
            ?? throw new ApiException(404, "Reservation not found.");
        return (await GetOwnedAsync(groupId, subject, cancellationToken)).InviteToken;
    }

    public async Task<GuestInvite> GetInviteAsync(string token, CancellationToken cancellationToken = default)
    {
        var groupId = await repository.FindGroupByInviteAsync(token, cancellationToken)
            ?? throw new ApiException(404, "Invitation not found.");
        var group = await repository.GetGroupAsync(groupId, cancellationToken)
            ?? throw new ApiException(404, "Group reservation not found");
        return group.Invite;
    }
}
