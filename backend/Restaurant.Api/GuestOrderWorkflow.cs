using System.Security.Cryptography;
using System.Text;

namespace Restaurant.Api;

// Domain-only building block. HTTP authorization and persistence must be supplied by the caller.
public sealed record GuestOrderLine(string Key, string DishId, string Name, string ForWhom, decimal UnitPrice, int Quantity);
public sealed record GuestLineNotice(string Key, string Message, string? DishName = null, string? ForWhom = null);
public sealed record ConfirmationChallenge(string Token, DateTimeOffset ExpiresAt);
public sealed record ConfirmationReceipt(string? GuestAccessToken);
// Persist only digests; raw confirmation and access tokens must never enter the database.
public sealed record GuestOrderState(
    DateTimeOffset ChangeDeadline, TimeSpan ConfirmationLifetime,
    GuestOrderLine[] Confirmed, GuestOrderLine[]? Pending, GuestLineNotice[] Notices,
    byte[]? ChallengeHash, byte[]? AccessHash, DateTimeOffset ChallengeExpires,
    long Revision, long ChallengeRevision, bool Cancelled);
public enum GuestOrderStatus { Choosing, AwaitingEmail, Confirmed, ChangesAwaitingConfirmation, Cancelled }

public sealed class GuestOrderWorkflow
{
    private List<GuestOrderLine> confirmed = [];
    private List<GuestOrderLine>? pending = [];
    private readonly List<GuestLineNotice> notices = [];
    private byte[]? challengeHash;
    private byte[]? accessHash;
    private DateTimeOffset challengeExpires;
    private long revision;
    private long challengeRevision;
    private bool cancelled;

    public GuestOrderWorkflow(DateTimeOffset changeDeadline, TimeSpan confirmationLifetime)
    {
        if (confirmationLifetime <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(confirmationLifetime));
        ChangeDeadline = changeDeadline;
        ConfirmationLifetime = confirmationLifetime;
    }

    public DateTimeOffset ChangeDeadline { get; }
    public TimeSpan ConfirmationLifetime { get; }
    public IReadOnlyList<GuestOrderLine> Confirmed => confirmed.ToArray();
    public IReadOnlyList<GuestOrderLine>? Pending => pending?.ToArray();
    public IReadOnlyList<GuestLineNotice> Notices => notices.ToArray();
    public decimal ConfirmedTotal => confirmed.Sum(line => line.UnitPrice * line.Quantity);
    public GuestOrderStatus Status => cancelled ? GuestOrderStatus.Cancelled
        : challengeHash is not null ? confirmed.Count == 0 ? GuestOrderStatus.AwaitingEmail : GuestOrderStatus.ChangesAwaitingConfirmation
        : pending is null ? confirmed.Count == 0 ? GuestOrderStatus.Choosing : GuestOrderStatus.Confirmed
        : confirmed.Count == 0 ? GuestOrderStatus.Choosing : GuestOrderStatus.ChangesAwaitingConfirmation;

    public GuestOrderState SaveState() => new(ChangeDeadline, ConfirmationLifetime,
        [.. confirmed], pending?.ToArray(), [.. notices], challengeHash?.ToArray(), accessHash?.ToArray(),
        challengeExpires, revision, challengeRevision, cancelled);

    public static GuestOrderWorkflow Restore(GuestOrderState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (state.Revision < 0 || state.ChallengeRevision > state.Revision
            || state.ChallengeHash is { Length: not 32 } || state.AccessHash is { Length: not 32 })
            throw new InvalidOperationException("Stored order state is invalid.");
        var order = new GuestOrderWorkflow(state.ChangeDeadline, state.ConfirmationLifetime)
        {
            confirmed = [.. state.Confirmed], pending = state.Pending?.ToList(),
            challengeHash = state.ChallengeHash?.ToArray(), accessHash = state.AccessHash?.ToArray(),
            challengeExpires = state.ChallengeExpires, revision = state.Revision,
            challengeRevision = state.ChallengeRevision, cancelled = state.Cancelled
        };
        order.notices.AddRange(state.Notices);
        return order;
    }

    // Called only after the server verifies owner permissions or a valid guest capability.
    public void Propose(GuestOrderLine line, DateTimeOffset now)
    {
        EnsureEditable(now);
        if (line.Quantity <= 0 || line.Quantity > 99 || line.UnitPrice < 0 || string.IsNullOrWhiteSpace(line.Key)
            || string.IsNullOrWhiteSpace(line.DishId) || string.IsNullOrWhiteSpace(line.Name))
            throw new ArgumentException("A valid dish and positive quantity are required.", nameof(line));
        var existing = confirmed.SingleOrDefault(item => item.Key == line.Key);
        if (existing is not null && (line.Quantity < existing.Quantity
            || line.DishId != existing.DishId || line.UnitPrice != existing.UnitPrice || line.ForWhom != existing.ForWhom))
            throw new InvalidOperationException("Use the decrease operation or a new line for other changes.");
        pending ??= [.. confirmed];
        pending.RemoveAll(item => item.Key == line.Key);
        pending.Add(line);
        notices.RemoveAll(item => item.Key == line.Key);
        InvalidateChallenge();
    }

    // Reductions and removals are effective immediately, even if an increase is pending.
    public void Reduce(string key, int newQuantity, DateTimeOffset now)
    {
        EnsureEditable(now);
        var existing = (pending ?? confirmed).SingleOrDefault(item => item.Key == key)
            ?? throw new KeyNotFoundException("Dish line not found.");
        if (newQuantity < 0 || newQuantity >= existing.Quantity) throw new ArgumentOutOfRangeException(nameof(newQuantity));
        confirmed = WithQuantity(confirmed, key, newQuantity);
        if (pending is not null) pending = WithQuantity(pending, key, newQuantity);
        notices.RemoveAll(item => item.Key == key);
        notices.Add(new(key, newQuantity == 0 ? "Страву скасовано" : $"Кількість порцій зменшена до {newQuantity}", existing.Name, existing.ForWhom));
        if (pending is not null && SameLines(confirmed, pending) && confirmed.Count > 0) pending = null;
        InvalidateChallenge();
    }

    public void Cancel(DateTimeOffset now)
    {
        EnsureEditable(now);
        foreach (var line in (pending ?? confirmed)) notices.Add(new(line.Key, "Страву скасовано", line.Name, line.ForWhom));
        confirmed.Clear();
        pending = null;
        cancelled = true;
        InvalidateChallenge();
    }

    public ConfirmationChallenge RequestConfirmation(DateTimeOffset now)
    {
        EnsureEditable(now);
        if (pending is null || pending.Count == 0) throw new InvalidOperationException("No unconfirmed dishes.");
        var token = NewToken();
        challengeHash = Hash(token);
        challengeRevision = revision;
        var expiry = now.Add(ConfirmationLifetime);
        challengeExpires = expiry < ChangeDeadline ? expiry : ChangeDeadline;
        return new(token, challengeExpires);
    }

    // This method must be invoked by explicit POST; opening an email link alone must not mutate data.
    public ConfirmationReceipt Confirm(string token, DateTimeOffset now)
    {
        ValidateConfirmation(token, now);
        confirmed = [.. pending!];
        pending = null;
        challengeHash = null;
        // The email link may be used on another device. Each successful confirmation
        // issues a fresh capability and revokes the previous one.
        var accessToken = NewToken();
        accessHash = Hash(accessToken);
        return new(accessToken);
    }

    public void ValidateConfirmation(string token, DateTimeOffset now)
    {
        EnsureEditable(now);
        if (challengeHash is null || pending is not { Count: > 0 } || now >= challengeExpires || challengeRevision != revision || !Matches(token, challengeHash))
            throw new UnauthorizedAccessException("Confirmation link is invalid, expired, or already used.");
    }

    public bool IsGuestAuthorized(string token) => accessHash is not null && Matches(token, accessHash);

    // Email ownership has already been verified by a separate one-time recovery challenge.
    // Restores access even after the edit deadline; never confirms pending dishes.
    public string RecoverAccess()
    {
        var token = NewToken();
        accessHash = Hash(token);
        challengeHash = null;
        return token;
    }

    private void EnsureEditable(DateTimeOffset now)
    {
        if (cancelled) throw new InvalidOperationException("Order is cancelled.");
        if (now >= ChangeDeadline) throw new InvalidOperationException("The change deadline has passed.");
    }
    private void InvalidateChallenge() { revision++; challengeHash = null; }
    private static string NewToken() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    private static byte[] Hash(string token) => SHA256.HashData(Encoding.UTF8.GetBytes(token ?? string.Empty));
    private static bool Matches(string token, byte[] expected) => CryptographicOperations.FixedTimeEquals(Hash(token), expected);
    private static List<GuestOrderLine> WithQuantity(List<GuestOrderLine> lines, string key, int quantity)
        => lines.Select(item => item.Key == key && item.Quantity > quantity ? item with { Quantity = quantity } : item)
            .Where(item => item.Quantity > 0).ToList();
    private static bool SameLines(List<GuestOrderLine> left, List<GuestOrderLine> right)
        => left.OrderBy(item => item.Key, StringComparer.Ordinal).SequenceEqual(right.OrderBy(item => item.Key, StringComparer.Ordinal));
}
