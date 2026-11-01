using System.Net.Mail;
using System.Security.Cryptography;
using System.Text;

namespace Restaurant.Api;

public sealed record GuestAccessRecoveryState(byte[]? TokenHash, DateTimeOffset ExpiresAt, DateTimeOffset LastRequestedAt);
public sealed record GuestAccessLink(string OrderId, string GuestName, string Token, DateTimeOffset ExpiresAt);
public sealed record GuestAccessMail(string To, string RestaurantName, string ReservationStart, GuestAccessLink[] Links);
public interface IGuestAccessMailer
{
    Task SendAsync(GuestAccessMail mail, CancellationToken cancellationToken);
}
public sealed class GuestAccessLinkException(string code, int status) : Exception
{
    public string Code { get; } = code;
    public int Status { get; } = status;
}

public sealed class GuestAccessRecoveryService(IGroupRepository groups, IGuestAccessMailer mailer, TimeProvider clock, IConfiguration? configuration = null)
{
    public async Task RequestForOrderAsync(string orderId, string email, CancellationToken ct = default)
    {
        var normalized = NormalizeEmail(email);
        var groupId = await groups.FindGroupBySessionAsync(orderId, ct);
        if (groupId is not null) await RequestAsync(groupId, normalized, orderId, ct);
    }
    public async Task RequestForInviteAsync(string inviteToken, string email, CancellationToken ct = default)
    {
        var normalized = NormalizeEmail(email);
        if (string.IsNullOrWhiteSpace(inviteToken)) return;
        var groupId = await groups.FindGroupByInviteAsync(inviteToken, ct);
        if (groupId is not null) await RequestAsync(groupId, normalized, null, ct);
    }
    private async Task RequestAsync(string groupId, string email, string? orderId, CancellationToken ct)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var group = await groups.GetGroupAsync(groupId, ct);
            if (group?.GuestOrders is null) return;
            var now = clock.GetUtcNow();
            var matches = group.GuestOrders.Values.Where(record => (orderId is null || record.Id == orderId)
                && group.Participants.Any(person => person.Id == record.Id && person.Type == "Guest")
                && string.Equals(record.Email, email, StringComparison.OrdinalIgnoreCase)
                && (record.AccessRecovery is null || now >= record.AccessRecovery.LastRequestedAt.AddMinutes(1))).ToArray();
            if (matches.Length == 0) return;
            var records = new Dictionary<string, StoredGuestOrder>(group.GuestOrders, StringComparer.Ordinal);
            var links = new List<GuestAccessLink>();
            foreach (var record in matches)
            {
                if (configuration?.GetValue<bool>("GuestMail:Background") == true)
                {
                    records[record.Id] = record with
                    {
                        AccessRecovery = new(null, now.AddMinutes(15), now),
                        RecoveryMail = new(Guid.NewGuid().ToString("N"), now, record.State.Revision),
                        Version = checked(record.Version + 1)
                    };
                    continue;
                }
                var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
                var expires = now.AddMinutes(15);
                records[record.Id] = record with
                {
                    AccessRecovery = new(Hash(token), expires, now), Version = checked(record.Version + 1)
                };
                links.Add(new(record.Id, record.GuestName, token, expires));
            }
            // Credential-only writes may run after submission. All dish state and group status stay intact.
            var updated = group with { GuestOrders = records, Version = checked(group.Version + 1) };
            if (!await groups.SaveAsync(updated, group.Version, null, ct)) continue;
            if (configuration?.GetValue<bool>("GuestMail:Background") == true) return;
            try { await mailer.SendAsync(new(matches[0].Email, group.Invite.RestaurantName, group.Invite.StartTime, links.ToArray()), ct); }
            catch (GuestMailUnavailableException) { /* Public response must not disclose whether this email owns orders. */ }
            return;
        }
        // A conflicting write does not disclose which orders match; the user may retry after a minute.
    }
    public async Task<ConfirmationReceipt> RedeemAsync(string orderId, string token, CancellationToken ct = default)
    {
        var groupId = await groups.FindGroupBySessionAsync(orderId, ct);
        var group = groupId is null ? null : await groups.GetGroupAsync(groupId, ct);
        var record = group?.GuestOrders?.GetValueOrDefault(orderId);
        if (group is null || record?.AccessRecovery?.TokenHash is not { Length: 32 } digest
            || !group.Participants.Any(person => person.Id == orderId && person.Type == "Guest")
            || string.IsNullOrEmpty(token) || token.Length > 128
            || !CryptographicOperations.FixedTimeEquals(Hash(token), digest))
            throw new GuestAccessLinkException("access_link_invalid_or_used", 401);
        if (clock.GetUtcNow() >= record.AccessRecovery.ExpiresAt) throw new GuestAccessLinkException("access_link_expired", 410);
        var flow = GuestOrderWorkflow.Restore(record.State);
        var access = flow.RecoverAccess();
        var restored = record with
        {
            State = flow.SaveState(), DraftAccessHash = null, Version = checked(record.Version + 1),
            AccessRecovery = record.AccessRecovery with { TokenHash = null }
        };
        var updated = group with
        {
            GuestOrders = new Dictionary<string, StoredGuestOrder>(group.GuestOrders!) { [orderId] = restored },
            Participants = group.Participants.Select(person => person.Id == orderId ? GroupGuestOrderStore.Project(restored) : person).ToArray(),
            Version = checked(group.Version + 1)
        };
        if (!await groups.SaveAsync(updated, group.Version, null, ct)) throw new ApiException(409, "Order changed. Retry opening the recovery link.");
        return new(access);
    }
    private static string NormalizeEmail(string email)
    {
        var value = (email ?? string.Empty).Trim();
        if (value.Length > 254 || !MailAddress.TryCreate(value, out var parsed)
            || !string.Equals(parsed.Address, value, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Invalid email.");
        return parsed.Address;
    }
    private static byte[] Hash(string value) => SHA256.HashData(Encoding.UTF8.GetBytes(value));
}
