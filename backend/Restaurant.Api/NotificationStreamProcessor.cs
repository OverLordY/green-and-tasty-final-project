namespace Restaurant.Api;

// The stream adapter must pass both images of the same group write. Only newly
// created intents trigger delivery: lease/progress writes must not form a loop.
public sealed class NotificationStreamProcessor(IGroupRepository groups, OwnerNotificationService sender, GuestMailDeliveryService? guestSender = null)
{
    public async Task ProcessAsync(GroupAggregate? previous, GroupAggregate current, CancellationToken ct = default)
    {
        if (previous is not null && previous.Id != current.Id)
            throw new InvalidOperationException("Mismatched group stream images.");

        var known = (previous?.Notifications ?? []).Select(n => n.Id).ToHashSet(StringComparer.Ordinal);
        var added = (current.Notifications ?? []).Where(n => !known.Contains(n.Id)).Select(n => n.Id).ToArray();
        var guestAdded = new List<(string OrderId, string IntentId, bool Recovery)>();
        foreach (var (orderId, order) in current.GuestOrders ?? [])
        {
            var old = previous?.GuestOrders?.GetValueOrDefault(orderId);
            if (order.ConfirmationMail is { } confirmation && confirmation.Id != old?.ConfirmationMail?.Id)
                guestAdded.Add((orderId, confirmation.Id, false));
            if (order.RecoveryMail is { } recovery && recovery.Id != old?.RecoveryMail?.Id)
                guestAdded.Add((orderId, recovery.Id, true));
        }
        if (added.Length == 0 && guestAdded.Count == 0) return;

        // Read the trusted current owner from storage, not the old event or a caller.
        var live = await groups.GetGroupAsync(current.Id, ct);
        if (live is null) return;
        var failed = false;
        foreach (var id in added)
        {
            ct.ThrowIfCancellationRequested();
            // An intent removed before processing needs no further action.
            if (!(live.Notifications ?? []).Any(n => n.Id == id)) continue;
            try
            {
                var result = await sender.SendAsync(live.Id, id, live.OwnerSubject, ct);
                if (!result.GuestAccepted || !result.OwnerAccepted) failed = true;
            }
            catch (ApiException e) when (e.Status == 409) { failed = true; }
        }
        if (guestAdded.Count > 0 && guestSender is null)
            throw new InvalidOperationException("Guest notification processor is not configured.");
        foreach (var item in guestAdded)
            if (!await guestSender!.SendAsync(live.Id, item.OrderId, item.IntentId, item.Recovery, ct)) failed = true;
        if (failed) throw new NotificationDeliveryPendingException();
    }
}

// Signals the event source to retry. Never include email addresses or tokens.
public sealed class NotificationDeliveryPendingException() : Exception("Notification delivery remains pending.");
