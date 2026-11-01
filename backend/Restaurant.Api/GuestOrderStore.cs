using System.Globalization;
using System.Text.Json;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;

namespace Restaurant.Api;

public sealed record StoredGuestOrder(
    string Id, GuestOrderState State, long Version, string GroupId = "",
    string GuestName = "", string Email = "", byte[]? DraftAccessHash = null,
    DateTimeOffset? LastConfirmationRequestedAt = null, string? OwnerChangeSummary = null,
    GuestAccessRecoveryState? AccessRecovery = null,
    GuestMailIntent? ConfirmationMail = null, GuestMailIntent? RecoveryMail = null);

// No raw email capability is persisted. The worker issues it only when sending.
public sealed record GuestMailIntent(string Id, DateTimeOffset RequestedAt, long Revision,
    DateTimeOffset? CompletedAt = null, string? Lease = null, DateTimeOffset? LeaseUntil = null);

public interface IGuestOrderStore
{
    Task<StoredGuestOrder?> FindAsync(string id, CancellationToken cancellationToken);
    Task<bool> CreateAsync(StoredGuestOrder order, CancellationToken cancellationToken);
    Task<bool> UpdateAsync(StoredGuestOrder order, long expectedVersion, CancellationToken cancellationToken);
}

// Guest records have separate, namespaced keys; writes reject stale concurrent versions.
public sealed class DynamoGuestOrderStore(IAmazonDynamoDB client, IConfiguration configuration) : IGuestOrderStore
{
    private readonly string table = configuration["DynamoDb:TableName"]
        ?? throw new InvalidOperationException("DynamoDb:TableName is required.");
    private static string Key(string id) => "GUESTORDER#" + id;

    public async Task<StoredGuestOrder?> FindAsync(string id, CancellationToken cancellationToken)
    {
        var response = await client.GetItemAsync(new GetItemRequest
        {
            TableName = table, ConsistentRead = true,
            Key = new() { ["PK"] = new() { S = Key(id) } }
        }, cancellationToken);
        if (response.Item is not { Count: > 0 }) return null;
        return JsonSerializer.Deserialize<StoredGuestOrder>(response.Item["data"].S)
            ?? throw new InvalidOperationException("Stored guest order is invalid.");
    }

    public Task<bool> CreateAsync(StoredGuestOrder order, CancellationToken cancellationToken)
    {
        if (order.Version != 0) throw new ArgumentOutOfRangeException(nameof(order));
        return PutAsync(order, null, cancellationToken);
    }

    public Task<bool> UpdateAsync(StoredGuestOrder order, long expectedVersion, CancellationToken cancellationToken)
    {
        if (expectedVersion < 0 || order.Version != checked(expectedVersion + 1))
            throw new ArgumentOutOfRangeException(nameof(order));
        return PutAsync(order, expectedVersion, cancellationToken);
    }

    private async Task<bool> PutAsync(StoredGuestOrder order, long? expected, CancellationToken cancellationToken)
    {
        var request = new PutItemRequest
        {
            TableName = table,
            Item = new()
            {
                ["PK"] = new() { S = Key(order.Id) },
                ["version"] = new() { N = order.Version.ToString(CultureInfo.InvariantCulture) },
                ["data"] = new() { S = JsonSerializer.Serialize(order) }
            },
            ConditionExpression = expected.HasValue ? "#v = :expected" : "attribute_not_exists(PK)",
            ExpressionAttributeNames = expected.HasValue ? new() { ["#v"] = "version" } : null,
            ExpressionAttributeValues = expected.HasValue
                ? new() { [":expected"] = new() { N = expected.Value.ToString(CultureInfo.InvariantCulture) } } : null
        };
        try { await client.PutItemAsync(request, cancellationToken); return true; }
        catch (ConditionalCheckFailedException) { return false; }
    }
}

// Isolated local tests/development only.
public sealed class InMemoryGuestOrderStore : IGuestOrderStore
{
    private readonly object gate = new();
    private readonly Dictionary<string, string> orders = new(StringComparer.Ordinal);
    private static StoredGuestOrder Copy(string value) => JsonSerializer.Deserialize<StoredGuestOrder>(value)!;
    public Task<StoredGuestOrder?> FindAsync(string id, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate) return Task.FromResult(orders.TryGetValue(id, out var value) ? Copy(value) : null);
    }
    public Task<bool> CreateAsync(StoredGuestOrder order, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (order.Version != 0) throw new ArgumentOutOfRangeException(nameof(order));
        lock (gate) return Task.FromResult(orders.TryAdd(order.Id, JsonSerializer.Serialize(order)));
    }
    public Task<bool> UpdateAsync(StoredGuestOrder order, long expectedVersion, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (expectedVersion < 0 || order.Version != checked(expectedVersion + 1)) throw new ArgumentOutOfRangeException(nameof(order));
        lock (gate)
        {
            if (!orders.TryGetValue(order.Id, out var value) || Copy(value).Version != expectedVersion) return Task.FromResult(false);
            orders[order.Id] = JsonSerializer.Serialize(order);
            return Task.FromResult(true);
        }
    }
}
