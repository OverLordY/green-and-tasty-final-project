using System.Net.Mail;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace Restaurant.Api;

public sealed record GuestDraft(string OrderId, string DraftAccessToken);
public sealed record GuestOrderView([property: JsonConverter(typeof(JsonStringEnumConverter))] GuestOrderStatus Status, GuestOrderLine[] Confirmed,
    GuestOrderLine[]? Pending, GuestLineNotice[] Notices, decimal ConfirmedTotal,
    string? GuestName = null, string? MaskedEmail = null, DateTimeOffset? ChangeDeadline = null,
    string? OwnerChangeSummary = null, bool? CanChange = null, bool SubmissionOverdue = false, bool ReservationCancelled = false);
public sealed record GuestConfirmationMail(
    string To, string GuestName, string GroupId, string OrderId,
    string ConfirmationToken, DateTimeOffset ExpiresAt, GuestOrderLine[] Items);
public interface IGuestConfirmationMailer
{
    Task SendAsync(GuestConfirmationMail message, CancellationToken cancellationToken);
}

// Application layer: the HTTP host must validate invitations/menus and provide server-owned prices.
public sealed class GuestOrderService(IGuestOrderStore store, IGuestConfirmationMailer mailer, IGroupRepository? groups = null, TimeProvider? clock = null, IConfiguration? configuration = null)
{
    private static readonly TimeSpan ResendDelay = TimeSpan.FromSeconds(30);
    public async Task<GuestOrderView> GetAsync(string orderId, string accessToken, CancellationToken cancellationToken = default)
    {
        var (record, order) = await AuthorizedAsync(orderId, accessToken, cancellationToken, false);
        var group = groups is null ? null : await groups.GetGroupAsync(record.GroupId, cancellationToken);
        if (groups is not null && (group is null || !group.Participants.Any(person => person.Id == record.Id)))
            throw new UnauthorizedAccessException("Access denied.");
        var now = (clock ?? TimeProvider.System).GetUtcNow();
        return PersonalView(record, order, now < order.ChangeDeadline
            && (groups is null || group?.Status == "Open")) with
        { SubmissionOverdue = group?.Status == "Open" && group.SubmissionDeadline is { } deadline && now >= deadline,
            ReservationCancelled = group?.ReservationCancelled == true };
    }
    public async Task<GuestDraft> CreateDraftAsync(
        string verifiedGroupId, string guestName, string email, DateTimeOffset changeDeadline,
        DateTimeOffset now, TimeSpan confirmationLifetime, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(verifiedGroupId) || string.IsNullOrWhiteSpace(guestName) || guestName.Trim().Length > 100)
            throw new ArgumentException("A group and guest name are required.");
        if (now >= changeDeadline) throw new InvalidOperationException("The change deadline has passed.");
        email = NormalizeEmail(email);
        var orderId = Guid.NewGuid().ToString("N");
        var token = NewToken();
        var state = new GuestOrderWorkflow(changeDeadline, confirmationLifetime).SaveState();
        var record = new StoredGuestOrder(orderId, state, 0, verifiedGroupId, guestName.Trim(), email, Hash(token));
        if (!await store.CreateAsync(record, cancellationToken)) throw new InvalidOperationException("Could not create order.");
        return new(orderId, token);
    }

    public async Task<GuestOrderView> ProposeAsync(string orderId, string accessToken, GuestOrderLine trustedLine,
        DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        var (record, order) = await AuthorizedAsync(orderId, accessToken, cancellationToken);
        order.Propose(trustedLine, now);
        await SaveAsync(record, order, cancellationToken);
        return PersonalView(record, order, now < order.ChangeDeadline);
    }

    public async Task<GuestOrderView> ReduceAsync(string orderId, string accessToken, string lineKey, int quantity,
        DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        var (record, order) = await AuthorizedAsync(orderId, accessToken, cancellationToken);
        order.Reduce(lineKey, quantity, now);
        await SaveAsync(record, order, cancellationToken);
        return PersonalView(record, order, now < order.ChangeDeadline);
    }

    public async Task RequestConfirmationAsync(string orderId, string accessToken, DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        var (record, order) = await AuthorizedAsync(orderId, accessToken, cancellationToken);
        if (record.LastConfirmationRequestedAt is { } last && now < last.Add(ResendDelay))
            throw new InvalidOperationException("Please wait before requesting another email.");
        var challenge = order.RequestConfirmation(now);
        var updated = record with
        {
            State = order.SaveState(), Version = checked(record.Version + 1), LastConfirmationRequestedAt = now,
            ConfirmationMail = configuration?.GetValue<bool>("GuestMail:Background") == true
                ? new(Guid.NewGuid().ToString("N"), now, order.SaveState().Revision) : record.ConfirmationMail
        };
        if (!await store.UpdateAsync(updated, record.Version, cancellationToken)) throw new InvalidOperationException("Order changed; try again.");
        if (configuration?.GetValue<bool>("GuestMail:Background") == true) return;
        // If delivery fails, the caller sees the failure and may explicitly retry; never report a sent email.
        await mailer.SendAsync(new(record.Email, record.GuestName, record.GroupId, record.Id, challenge.Token,
            challenge.ExpiresAt, order.Pending!.ToArray()), cancellationToken);
    }

    // Only expose this operation as an explicit POST, never as a link-opening GET.
    public async Task<ConfirmationPreview> PreviewAsync(string orderId, string confirmationToken, DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        var record = await store.FindAsync(orderId, cancellationToken) ?? throw new KeyNotFoundException("Order not found.");
        await EnsureGroupOpenAsync(record, cancellationToken);
        var order = GuestOrderWorkflow.Restore(record.State);
        order.ValidateConfirmation(confirmationToken, now);
        return new(order.Confirmed.ToArray(), order.Pending!.ToArray());
    }

    public async Task<ConfirmationReceipt> ConfirmAsync(string orderId, string confirmationToken, DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        var record = await store.FindAsync(orderId, cancellationToken) ?? throw new KeyNotFoundException("Order not found.");
        var order = GuestOrderWorkflow.Restore(record.State);
        await EnsureGroupOpenAsync(record, cancellationToken);
        var receipt = order.Confirm(confirmationToken, now);
        var updated = record with { DraftAccessHash = null, State = order.SaveState(), Version = checked(record.Version + 1) };
        if (!await store.UpdateAsync(updated, record.Version, cancellationToken)) throw new InvalidOperationException("Order changed; try again.");
        return receipt;
    }

    private async Task<(StoredGuestOrder Record, GuestOrderWorkflow Order)> AuthorizedAsync(
        string orderId, string accessToken, CancellationToken cancellationToken, bool requireOpen = true)
    {
        var record = await store.FindAsync(orderId, cancellationToken) ?? throw new UnauthorizedAccessException("Access denied.");
        var order = GuestOrderWorkflow.Restore(record.State);
        var draftValid = record.DraftAccessHash is { Length: 32 }
            && CryptographicOperations.FixedTimeEquals(Hash(accessToken), record.DraftAccessHash);
        if (!draftValid && !order.IsGuestAuthorized(accessToken)) throw new UnauthorizedAccessException("Access denied.");
        if (requireOpen) await EnsureGroupOpenAsync(record, cancellationToken);
        return (record, order);
    }

    private async Task EnsureGroupOpenAsync(StoredGuestOrder record, CancellationToken cancellationToken)
    {
        if (groups is null) return; // Standalone domain tests; the HTTP host supplies the repository.
        var group = await groups.GetGroupAsync(record.GroupId, cancellationToken);
        if (group is null || group.Status != "Open") throw new InvalidOperationException("The group is not open.");
    }

    private async Task SaveAsync(StoredGuestOrder record, GuestOrderWorkflow order, CancellationToken cancellationToken)
    {
        var updated = record with { State = order.SaveState(), Version = checked(record.Version + 1) };
        if (!await store.UpdateAsync(updated, record.Version, cancellationToken)) throw new InvalidOperationException("Order changed; try again.");
    }

    private static string NormalizeEmail(string email)
    {
        var trimmed = (email ?? string.Empty).Trim();
        if (trimmed.Length > 254 || !MailAddress.TryCreate(trimmed, out var address)
            || !string.Equals(address.Address, trimmed, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("A valid email address is required.", nameof(email));
        return address.Address;
    }
    private static string NewToken() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    private static byte[] Hash(string token) => SHA256.HashData(Encoding.UTF8.GetBytes(token ?? string.Empty));
    private static GuestOrderView View(GuestOrderWorkflow order) => new(order.Status, order.Confirmed.ToArray(),
        order.Pending?.ToArray(), order.Notices.ToArray(), order.ConfirmedTotal);
    private static GuestOrderView PersonalView(StoredGuestOrder record, GuestOrderWorkflow order, bool canChange)
    {
        var separator = record.Email.IndexOf('@');
        return View(order) with
        {
            GuestName = record.GuestName, ChangeDeadline = order.ChangeDeadline, OwnerChangeSummary = record.OwnerChangeSummary,
            MaskedEmail = separator > 0 ? record.Email[..1] + "***" + record.Email[separator..] : "***",
            CanChange = canChange && order.Status != GuestOrderStatus.Cancelled
        };
    }
}
