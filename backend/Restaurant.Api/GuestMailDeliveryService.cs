using System.Security.Cryptography;
using System.Text;

namespace Restaurant.Api;

// Durable delivery for guest-requested mail. Capabilities are generated in memory;
// only hashes, leases and delivery status are saved in the group aggregate.
public sealed class GuestMailDeliveryService(IGroupRepository groups, IGuestConfirmationMailer confirmationMailer,
    IGuestAccessMailer accessMailer, TimeProvider clock)
{
    public async Task<bool> SendAsync(string groupId, string orderId, string intentId, bool recovery, CancellationToken ct = default)
    {
        var group = await groups.GetGroupAsync(groupId, ct);
        var record = group?.GuestOrders?.GetValueOrDefault(orderId);
        var intent = recovery ? record?.RecoveryMail : record?.ConfirmationMail;
        if (group is null || record is null || intent is null || intent.Id != intentId || intent.CompletedAt is not null
            || !group.Participants.Any(p => p.Id == orderId && p.Type == "Guest")) return true;
        var now = clock.GetUtcNow();
        if (intent.LeaseUntil > now) return false;
        if (recovery ? now >= intent.RequestedAt.AddMinutes(15)
            : group.Status != "Open" || now >= record.State.ChangeDeadline || record.State.Revision != intent.Revision
                || record.State.Pending is not { Length: > 0 }) return true;

        var lease = Guid.NewGuid().ToString("N");
        var locked = intent with { Lease = lease, LeaseUntil = now.AddMinutes(2) };
        GuestConfirmationMail? confirmation = null;
        GuestAccessMail? access = null;
        if (recovery)
        {
            var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            var expires = intent.RequestedAt.AddMinutes(15);
            record = record with { AccessRecovery = new(SHA256.HashData(Encoding.UTF8.GetBytes(token)), expires, intent.RequestedAt), RecoveryMail = locked };
            access = new(record.Email, group.Invite.RestaurantName, group.Invite.StartTime, [new(record.Id, record.GuestName, token, expires)]);
        }
        else
        {
            var flow = GuestOrderWorkflow.Restore(record.State);
            var challenge = flow.RequestConfirmation(now);
            record = record with { State = flow.SaveState(), ConfirmationMail = locked };
            confirmation = new(record.Email, record.GuestName, group.Id, record.Id, challenge.Token, challenge.ExpiresAt, flow.Pending!.ToArray());
        }
        record = record with { Version = checked(record.Version + 1) };
        if (!await SaveAsync(group, record, ct)) return false;
        var accepted = false;
        try
        {
            if (recovery) await accessMailer.SendAsync(access!, ct);
            else await confirmationMailer.SendAsync(confirmation!, ct);
            accepted = true;
        }
        catch (GuestMailUnavailableException) { /* The stream retries this intent. */ }
        finally { await ReleaseAsync(groupId, orderId, intentId, recovery, lease, accepted, ct); }
        return accepted;
    }

    private async Task ReleaseAsync(string groupId, string orderId, string intentId, bool recovery, string lease, bool accepted, CancellationToken ct)
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            var group = await groups.GetGroupAsync(groupId, ct);
            var record = group?.GuestOrders?.GetValueOrDefault(orderId);
            var intent = recovery ? record?.RecoveryMail : record?.ConfirmationMail;
            if (group is null || record is null || intent is null || intent.Id != intentId || intent.Lease != lease) return;
            var next = intent with { Lease = null, LeaseUntil = null, CompletedAt = accepted ? clock.GetUtcNow() : null };
            record = recovery ? record with { RecoveryMail = next } : record with { ConfirmationMail = next };
            record = record with { Version = checked(record.Version + 1) };
            if (await SaveAsync(group, record, ct)) return;
        }
        throw new InvalidOperationException("Guest email delivery progress changed concurrently.");
    }

    private Task<bool> SaveAsync(GroupAggregate group, StoredGuestOrder record, CancellationToken ct)
    {
        var records = new Dictionary<string, StoredGuestOrder>(group.GuestOrders!) { [record.Id] = record };
        var participants = group.Participants.Select(p => p.Id == record.Id ? GroupGuestOrderStore.Project(record) : p).ToArray();
        return groups.SaveAsync(group with { GuestOrders = records, Participants = participants, Version = checked(group.Version + 1) }, group.Version, null, ct);
    }
}
