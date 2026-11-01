namespace Restaurant.Api;

public sealed record OrderChangeMail(string To, string Summary, GuestOrderLine[] Items,
    bool NeedsConfirmation, GuestConfirmationMail? Confirmation = null, bool Superseded = false, bool IsReservationChange = false);
public interface IOrderChangeMailer
{
    Task SendAsync(OrderChangeMail message, CancellationToken cancellationToken);
}
public sealed record NotificationDelivery(string Id, bool GuestAccepted, bool OwnerAccepted, string? Summary = null);

// Durable intent is created by the owner mutation. A bounded lease prevents simultaneous retries.
// SES acceptance is recorded per recipient; it is not a claim of delivery to the inbox.
public sealed class OwnerNotificationService(IGroupRepository groups, IOrderChangeMailer mailer, TimeProvider clock)
{
    public async Task<NotificationDelivery> SendAsync(string groupId, string id, string subject, CancellationToken ct = default)
    {
        var group = await LoadAsync(groupId, subject, ct);
        var intent = group.Notifications?.SingleOrDefault(n => n.Id == id) ?? throw new ApiException(404, "Notification not found.");
        if (intent.GuestSentAt is not null && intent.OwnerSentAt is not null) return Status(intent);
        var now = clock.GetUtcNow();
        if (intent.LeaseUntil > now) throw new ApiException(409, "Notification is being sent. Try again shortly.");
        var lease = Guid.NewGuid().ToString("N");
        intent = intent with { Lease = lease, LeaseUntil = now.AddMinutes(2) };
        GuestConfirmationMail? confirmation = null;
        var current = group.GuestOrders?.GetValueOrDefault(intent.GuestOrderId);
        var superseded = (current is null && intent.GuestOrderId != group.OwnerSessionId)
            || (current is not null && current.State.Revision != intent.Revision)
            || !group.Participants.Any(p => p.Id == intent.GuestOrderId) || group.Status != "Open"
            || (intent.NeedsGuestConfirmation && (current is null || now >= current.State.ChangeDeadline))
            || group.Notifications!.SkipWhile(n => n.Id != id).Skip(1).Any(n => n.GuestOrderId == intent.GuestOrderId && n.NeedsGuestConfirmation);
        if (!superseded && intent.GuestSentAt is null && intent.NeedsGuestConfirmation
            && current!.State.Pending is { Length: > 0 } && now < current.State.ChangeDeadline)
        {
            var flow = GuestOrderWorkflow.Restore(current.State);
            var challenge = flow.RequestConfirmation(now);
            var record = current with { State = flow.SaveState(), Version = checked(current.Version + 1), LastConfirmationRequestedAt = now };
            confirmation = new(record.Email, record.GuestName, group.Id, record.Id, challenge.Token, challenge.ExpiresAt, flow.Pending!.ToArray());
            group = group with
            {
                GuestOrders = new Dictionary<string, StoredGuestOrder>(group.GuestOrders!) { [record.Id] = record },
                Participants = group.Participants.Select(p => p.Id == record.Id ? GroupGuestOrderStore.Project(record) : p).ToArray()
            };
        }
        group = Replace(group, intent);
        await SaveAsync(group, ct);
        var guestAccepted = intent.GuestSentAt is not null;
        try
        {
            if (intent.GuestSentAt is null)
            {
                try
                {
                    await mailer.SendAsync(new(intent.GuestEmail, intent.Summary, intent.Items, intent.NeedsGuestConfirmation, confirmation, superseded, intent.IsReservationChange), ct);
                    await MarkAsync(groupId, id, subject, lease, true, false, false, ct);
                    guestAccepted = true;
                }
                catch (GuestMailUnavailableException) { /* Other recipient must still be attempted. */ }
            }
            if (intent.OwnerSentAt is null)
            {
                // Never disclose the guest's confirmation or access capability to the organizer.
                try
                {
                    if (!guestAccepted || !string.Equals(intent.GuestEmail, intent.OwnerEmail, StringComparison.OrdinalIgnoreCase))
                        await mailer.SendAsync(new(intent.OwnerEmail, intent.Summary, intent.Items, intent.NeedsGuestConfirmation, null, superseded, intent.IsReservationChange), ct);
                    await MarkAsync(groupId, id, subject, lease, false, true, false, ct);
                }
                catch (GuestMailUnavailableException) { /* Keep this recipient pending for retry. */ }
            }
        }
        finally { await MarkAsync(groupId, id, subject, lease, false, false, true, ct); }
        return Status((await LoadAsync(groupId, subject, ct)).Notifications!.Single(n => n.Id == id));
    }

    public async Task<NotificationDelivery[]> StatusAsync(string groupId, string subject, CancellationToken ct = default)
        => ((await LoadAsync(groupId, subject, ct)).Notifications ?? []).Select(Status).ToArray();

    private async Task MarkAsync(string groupId, string id, string subject, string lease, bool guest, bool owner, bool release, CancellationToken ct)
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            var group = await LoadAsync(groupId, subject, ct);
            var intent = group.Notifications!.Single(n => n.Id == id);
            if (intent.Lease != lease) return;
            intent = intent with
            {
                GuestSentAt = guest ? clock.GetUtcNow() : intent.GuestSentAt,
                OwnerSentAt = owner ? clock.GetUtcNow() : intent.OwnerSentAt,
                Lease = release ? null : lease, LeaseUntil = release ? null : intent.LeaseUntil
            };
            if (await groups.SaveAsync(Replace(group, intent) with { Version = checked(group.Version + 1) }, group.Version, null, ct)) return;
        }
        throw new ApiException(409, "Notification progress changed concurrently. Refresh its status.");
    }
    private async Task<GroupAggregate> LoadAsync(string id, string subject, CancellationToken ct)
    {
        var group = await groups.GetGroupAsync(id, ct) ?? throw new ApiException(404, "Group not found.");
        if (string.IsNullOrWhiteSpace(subject) || group.OwnerSubject != subject) throw new ApiException(403, "Only the Owner may manage notifications.");
        return group;
    }
    private async Task SaveAsync(GroupAggregate group, CancellationToken ct)
    {
        if (!await groups.SaveAsync(group with { Version = checked(group.Version + 1) }, group.Version, null, ct))
            throw new ApiException(409, "The order changed. Retry the notification.");
    }
    private static GroupAggregate Replace(GroupAggregate group, OrderChangeNotification intent)
        => group with { Notifications = group.Notifications!.Select(n => n.Id == intent.Id ? intent : n).ToArray() };
    private static NotificationDelivery Status(OrderChangeNotification n) => new(n.Id, n.GuestSentAt is not null, n.OwnerSentAt is not null, n.Summary);
}
