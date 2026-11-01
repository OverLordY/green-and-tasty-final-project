using System.Net.Mail;

namespace Restaurant.Api;

public sealed record OrderAuditEntry(string Id, string ActorSubject, string GuestOrderId, string Action,
    DateTimeOffset At, string Summary);
// Persist notification intent together with the change. No raw access/confirmation token is stored here.
public sealed record OrderChangeNotification(string Id, string GuestOrderId, string GuestEmail, string OwnerEmail,
    string Summary, bool NeedsGuestConfirmation, DateTimeOffset CreatedAt, long Revision,
    GuestOrderLine[] Items, DateTimeOffset? GuestSentAt = null, DateTimeOffset? OwnerSentAt = null,
    string? Lease = null, DateTimeOffset? LeaseUntil = null, bool IsReservationChange = false);
public sealed record OwnerLineChange(string DishId, string ForWhom, int Quantity);
public sealed record OwnerChangeResult(GroupSnapshot Snapshot, string NotificationId);
public sealed record OwnerBulkChangeResult(GroupSnapshot Snapshot, string[] NotificationIds);
public sealed record OwnerOrderMenu(MenuEntry[] Items, DateTimeOffset ChangeDeadline, bool CanChange);

public sealed class OwnerGuestOrderService(IGroupRepository groups, IConfiguration settings, TimeProvider clock)
{
    public async Task<GroupSnapshot> RemoveAsync(string groupId, string orderId, string subject, string? verifiedEmail, CancellationToken ct = default)
    {
        var (group, email) = await OwnedAsync(groupId, subject, verifiedEmail, ct);
        var record = ActiveGuest(group, orderId);
        var flow = GuestOrderWorkflow.Restore(record.State);
        var now = clock.GetUtcNow();
        try { flow.Cancel(now); }
        catch (InvalidOperationException exception) { throw new ApiException(409, exception.Message); }
        var change = BuildChange(group, record, flow, subject, email,
            $"Організатор видалив гостя {record.GuestName} із групи та скасував його страви", "RemoveGuest", now);
        var updated = change.Group with { Participants = change.Group.Participants.Where(p => p.Id != orderId).ToArray() };
        if (!await groups.SaveAsync(updated, group.Version, orderId, ct)) throw new ApiException(409, "The group changed. Refresh and try again.");
        return updated.Snapshot() with { NotificationId = change.NotificationId };
    }

    public async Task<OwnerChangeResult> RemindAsync(string groupId, string orderId, string subject,
        string? verifiedEmail, CancellationToken ct = default)
    {
        var (group, email) = await OwnedAsync(groupId, subject, verifiedEmail, ct);
        var record = ActiveGuest(group, orderId);
        var now = clock.GetUtcNow();
        if (now >= record.State.ChangeDeadline) throw new ApiException(409, "The change deadline has passed.");
        if (record.State.Pending is not { Length: > 0 }) throw new ApiException(400, "No dishes awaiting confirmation.");
        if ((group.Notifications ?? []).Any(n => n.GuestOrderId == orderId && n.CreatedAt.AddMinutes(1) > now))
            throw new ApiException(429, "Please wait one minute before another reminder.");
        return await CommitAsync(group, record, GuestOrderWorkflow.Restore(record.State), subject, email,
            $"Організатор нагадує: підтвердіть замовлення гостя {record.GuestName}", "Reminder", now, ct);
    }

    public async Task<OwnerBulkChangeResult> ClearAllAsync(string groupId, string subject, string? verifiedEmail, CancellationToken ct = default)
    {
        var (original, email) = await OwnedAsync(groupId, subject, verifiedEmail, ct);
        var now = clock.GetUtcNow();
        var group = original;
        var ids = new List<string>();
        foreach (var person in original.Participants.Where(p => p.Type == "Guest" && (p.Items.Length > 0 || p.PendingItems is { Length: > 0 })))
        {
            var record = ActiveGuest(group, person.Id);
            var flow = GuestOrderWorkflow.Restore(record.State);
            try { foreach (var line in (flow.Pending ?? flow.Confirmed).ToArray()) flow.Reduce(line.Key, 0, now); }
            catch (InvalidOperationException exception) { throw new ApiException(409, exception.Message); }
            var change = BuildChange(group, record, flow, subject, email,
                $"Організатор очистив замовлення гостя {record.GuestName}", "ClearGroup", now);
            group = change.Group; ids.Add(change.NotificationId);
        }
        var owner = original.Participants.Single(p => p.Id == original.OwnerSessionId);
        if (owner.Items.Length > 0)
        {
            var start = DateTimeOffset.Parse(original.Invite.StartTime, System.Globalization.CultureInfo.InvariantCulture);
            var deadline = OrderDeadlines.FromReservation(start,
                TimeSpan.FromMinutes(settings.GetValue<int?>("Restaurant:PreparationMinutes") ?? 30),
                TimeSpan.FromMinutes(settings.GetValue<int?>("Restaurant:ReviewMinutes") ?? 15)).ChangesClose;
            if (now >= deadline) throw new ApiException(409, "The change deadline has passed.");
            var id = Guid.NewGuid().ToString("N");
            const string summary = "Організатор очистив власні страви разом із замовленнями групи";
            group = group with
            {
                Participants = group.Participants.Select(p => p.Id == owner.Id ? p with { Items = [], PendingItems = null, IsSubmitted = false, OwnerChangeSummary = summary } : p).ToArray(),
                Audit = [.. group.Audit ?? [], new(Guid.NewGuid().ToString("N"), subject, owner.Id, "ClearGroup", now, summary)],
                Notifications = [.. group.Notifications ?? [], new(id, owner.Id, email, email, summary, false, now, 0, [])]
            };
            ids.Add(id);
        }
        if (ids.Count == 0) throw new ApiException(400, "The group order is already empty.");
        if ((group.Audit?.Length ?? 0) > 500 || (group.Notifications?.Length ?? 0) > 200)
            throw new ApiException(409, "Notification/history capacity reached; contact the restaurant.");
        group = group with { Version = checked(original.Version + 1), OwnerEmail = email };
        if (!await groups.SaveAsync(group, original.Version, null, ct)) throw new ApiException(409, "The group changed. Refresh and try again.");
        return new(group.Snapshot(), ids.ToArray());
    }

    public async Task<OwnerOrderMenu> MenuAsync(string groupId, string subject, CancellationToken ct = default)
    {
        var group = await groups.GetGroupAsync(groupId, ct) ?? throw new ApiException(404, "Group not found.");
        if (string.IsNullOrWhiteSpace(subject) || group.OwnerSubject != subject) throw new ApiException(403, "Only the Owner may edit this order.");
        var start = DateTimeOffset.Parse(group.Invite.StartTime, System.Globalization.CultureInfo.InvariantCulture);
        var deadline = OrderDeadlines.FromReservation(start,
            TimeSpan.FromMinutes(settings.GetValue<int?>("Restaurant:PreparationMinutes") ?? 30),
            TimeSpan.FromMinutes(settings.GetValue<int?>("Restaurant:ReviewMinutes") ?? 15)).ChangesClose;
        return new(RestaurantMenu.ForLocation(settings, group.Invite.LocationId), deadline,
            group.Status == "Open" && clock.GetUtcNow() < deadline);
    }

    public async Task<GroupSnapshot> ChangeOwnAsync(string groupId, string subject, string? verifiedEmail, OwnerLineChange request, CancellationToken ct = default)
    {
        var (group, _) = await OwnedAsync(groupId, subject, verifiedEmail, ct);
        var menu = await MenuAsync(groupId, subject, ct);
        if (!menu.CanChange) throw new ApiException(409, "The change deadline has passed.");
        if (string.IsNullOrWhiteSpace(request.DishId) || string.IsNullOrWhiteSpace(request.ForWhom)
            || request.ForWhom.Trim().Length > 100 || request.ForWhom.Contains('@') || request.Quantity < 0 || request.Quantity > 99)
            throw new ApiException(400, "Invalid dish, recipient or quantity.");
        var owner = group.Participants.SingleOrDefault(person => person.Id == group.OwnerSessionId && person.Type == "Owner")
            ?? throw new ApiException(404, "Owner session not found.");
        var recipient = request.ForWhom.Trim();
        var key = request.DishId + ":" + recipient;
        var current = owner.Items.SingleOrDefault(item => item.LineKey == key);
        if (current is null && request.Quantity == 0 || current?.Quantity == request.Quantity)
            throw new ApiException(400, "No quantity change.");
        var items = owner.Items.Where(item => item.LineKey != key).ToList();
        if (request.Quantity > 0)
        {
            var dish = current?.Dish;
            if (current is null || request.Quantity > current.Quantity)
            {
                var selected = menu.Items.SingleOrDefault(item => item.Id == request.DishId) ?? throw new ApiException(404, "Dish not found.");
                if (!selected.Available) throw new ApiException(400, "Dish is unavailable.");
                dish ??= new Dish(selected.Id, selected.Name, selected.Price, selected.ImageUrl, selected.Weight);
            }
            items.Add(new CartItem(dish!, request.Quantity, recipient, key));
        }
        if (items.Count > 100 || (group.Audit?.Length ?? 0) >= 500) throw new ApiException(409, "Order capacity reached.");
        var now = clock.GetUtcNow();
        var updated = group with {
            Participants = group.Participants.Select(person => person.Id == owner.Id ? person with { Items = items.ToArray(), IsSubmitted = items.Count > 0 } : person).ToArray(),
            Audit = [.. group.Audit ?? [], new(Guid.NewGuid().ToString("N"), subject, owner.Id, "ChangeOwnOrder", now, "Організатор змінив власні страви")],
            Version = checked(group.Version + 1)
        };
        if (!await groups.SaveAsync(updated, group.Version, null, ct)) throw new ApiException(409, "The group changed. Refresh and try again.");
        return updated.Snapshot();
    }

    public async Task<OwnerChangeResult> ChangeAsync(string groupId, string orderId, string subject,
        string? verifiedEmail, OwnerLineChange request, CancellationToken cancellationToken = default)
    {
        var (group, ownerEmail) = await OwnedAsync(groupId, subject, verifiedEmail, cancellationToken);
        var record = ActiveGuest(group, orderId);
        var flow = GuestOrderWorkflow.Restore(record.State);
        var now = clock.GetUtcNow();
        if (string.IsNullOrWhiteSpace(request.DishId) || string.IsNullOrWhiteSpace(request.ForWhom)
            || request.ForWhom.Length > 100 || request.Quantity < 0 || request.Quantity > 99)
            throw new ApiException(400, "Invalid dish, recipient or quantity.");
        var recipient = request.ForWhom.Trim();
        var key = request.DishId + ":" + recipient;
        var current = (flow.Pending ?? flow.Confirmed).SingleOrDefault(line => line.Key == key);
        if (current is null && recipient.Contains('@'))
            throw new ApiException(400, "Enter a person's name, not an email address. Confirmation goes to the owner of this order.");
        string dishName;
        try
        {
            if (current is not null && request.Quantity < current.Quantity)
            {
                dishName = current.Name; flow.Reduce(key, request.Quantity, now);
            }
            else
            {
                if (request.Quantity == 0 || current?.Quantity == request.Quantity) throw new ApiException(400, "No quantity change.");
                var menu = RestaurantMenu.ForLocation(settings, group.Invite.LocationId);
                var dish = menu.SingleOrDefault(item => item.Id == request.DishId)
                    ?? throw new ApiException(404, "Dish not found.");
                if (!dish.Available) throw new ApiException(400, "Dish is unavailable.");
                dishName = dish.Name;
                flow.Propose(new(key, dish.Id, dish.Name, recipient, dish.Price, request.Quantity), now);
            }
        }
        catch (InvalidOperationException exception) { throw new ApiException(409, exception.Message); }
        var summary = $"Організатор змінив замовлення гостя {record.GuestName}: {dishName} для {recipient}, {current?.Quantity ?? 0} → {request.Quantity}";
        return await CommitAsync(group, record, flow, subject, ownerEmail, summary, "ChangeQuantity", now, cancellationToken);
    }

    public async Task<OwnerChangeResult> ClearAsync(string groupId, string orderId, string subject,
        string? verifiedEmail, CancellationToken cancellationToken = default)
    {
        var (group, ownerEmail) = await OwnedAsync(groupId, subject, verifiedEmail, cancellationToken);
        var record = ActiveGuest(group, orderId);
        var flow = GuestOrderWorkflow.Restore(record.State);
        var now = clock.GetUtcNow();
        try { foreach (var line in (flow.Pending ?? flow.Confirmed).ToArray()) flow.Reduce(line.Key, 0, now); }
        catch (InvalidOperationException exception) { throw new ApiException(409, exception.Message); }
        if (record.State.Confirmed.Length == 0 && record.State.Pending is not { Length: > 0 })
            throw new ApiException(400, "The order is already empty.");
        return await CommitAsync(group, record, flow, subject, ownerEmail,
            $"Організатор очистив замовлення гостя {record.GuestName}", "ClearOrder", now, cancellationToken);
    }

    public async Task<OrderAuditEntry[]> HistoryAsync(string groupId, string subject, CancellationToken cancellationToken = default)
    {
        var group = await groups.GetGroupAsync(groupId, cancellationToken) ?? throw new ApiException(404, "Group not found.");
        if (group.OwnerSubject != subject) throw new ApiException(403, "Only the Owner may view this history.");
        return group.Audit ?? [];
    }

    private async Task<(GroupAggregate Group, string Email)> OwnedAsync(string id, string subject, string? verifiedEmail, CancellationToken cancellationToken)
    {
        var group = await groups.GetGroupAsync(id, cancellationToken) ?? throw new ApiException(404, "Group not found.");
        if (string.IsNullOrWhiteSpace(subject) || group.OwnerSubject != subject) throw new ApiException(403, "Only the Owner may change this order.");
        if (group.Status != "Open") throw new ApiException(409, "The group is not open.");
        // Only a server-stored address or a verified claim from the authenticated token is accepted.
        var email = group.OwnerEmail ?? verifiedEmail;
        if (string.IsNullOrWhiteSpace(email) || !MailAddress.TryCreate(email, out var parsed) || parsed.Address != email)
            throw new ApiException(409, "A verified organizer email is required for change notifications.");
        return (group, email);
    }
    private static StoredGuestOrder ActiveGuest(GroupAggregate group, string orderId)
    {
        if (!group.Participants.Any(person => person.Id == orderId && person.Type == "Guest")
            || group.GuestOrders?.GetValueOrDefault(orderId) is not { } record)
            throw new ApiException(404, "Guest order not found in this group.");
        return record;
    }
    private async Task<OwnerChangeResult> CommitAsync(GroupAggregate group, StoredGuestOrder previous, GuestOrderWorkflow flow,
        string subject, string ownerEmail, string summary, string action, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var change = BuildChange(group, previous, flow, subject, ownerEmail, summary, action, now);
        if (!await groups.SaveAsync(change.Group, group.Version, null, cancellationToken)) throw new ApiException(409, "The group changed. Refresh and try again.");
        return new(change.Group.Snapshot(), change.NotificationId);
    }

    private static (GroupAggregate Group, string NotificationId) BuildChange(GroupAggregate group, StoredGuestOrder previous,
        GuestOrderWorkflow flow, string subject, string ownerEmail, string summary, string action, DateTimeOffset now)
    {
        if ((group.Audit?.Length ?? 0) >= 500 || (group.Notifications?.Length ?? 0) >= 200)
            throw new ApiException(409, "Notification/history capacity reached; contact the restaurant.");
        var record = previous with { State = flow.SaveState(), Version = checked(previous.Version + 1),
            OwnerChangeSummary = action == "Reminder" ? previous.OwnerChangeSummary : summary };
        var records = new Dictionary<string, StoredGuestOrder>(group.GuestOrders!, StringComparer.Ordinal) { [record.Id] = record };
        var notification = new OrderChangeNotification(Guid.NewGuid().ToString("N"), record.Id, record.Email, ownerEmail,
            summary, flow.Pending is { Count: > 0 }, now, record.State.Revision,
            (flow.Pending ?? flow.Confirmed).ToArray());
        var audit = new OrderAuditEntry(Guid.NewGuid().ToString("N"), subject, record.Id, action, now, summary);
        var updated = group with
        {
            GuestOrders = records, OwnerEmail = ownerEmail, Version = checked(group.Version + 1),
            Participants = group.Participants.Select(person => person.Id == record.Id ? GroupGuestOrderStore.Project(record) : person).ToArray(),
            Audit = [.. group.Audit ?? [], audit], Notifications = [.. group.Notifications ?? [], notification]
        };
        return (updated, notification.Id);
    }
}
