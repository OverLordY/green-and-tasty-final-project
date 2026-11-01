using System.Globalization;
using System.Text.Json;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;

namespace Restaurant.Api;

// One group is one bounded aggregate. Conditional versions prevent lost updates.
// Reservation/session/invite lookups are separate items in the same PK-only table.
public sealed class DynamoGroupRepository(IAmazonDynamoDB client, IConfiguration configuration) : IGroupRepository
{
    private readonly string table = configuration["DynamoDb:TableName"]
        ?? throw new InvalidOperationException("DynamoDb:TableName is required.");

    private async Task<Dictionary<string, AttributeValue>?> ReadAsync(string key, CancellationToken cancellationToken)
    {
        var response = await client.GetItemAsync(new GetItemRequest
        {
            TableName = table, ConsistentRead = true,
            Key = new() { ["PK"] = new() { S = key } }
        }, cancellationToken);
        return response.Item is { Count: > 0 } ? response.Item : null;
    }

    public async Task<GroupAggregate?> GetGroupAsync(string groupId, CancellationToken cancellationToken)
    {
        var item = await ReadAsync("GROUP#" + groupId, cancellationToken);
        return item is null ? null : JsonSerializer.Deserialize<GroupAggregate>(item["data"].S)
            ?? throw new InvalidOperationException("Stored group data is invalid.");
    }

    private async Task<string?> LookupAsync(string key, CancellationToken cancellationToken)
        => (await ReadAsync(key, cancellationToken))?.GetValueOrDefault("groupId")?.S;

    public Task<string?> FindGroupByReservationAsync(string reservationId, CancellationToken cancellationToken)
        => LookupAsync("RESERVATION#" + reservationId, cancellationToken);
    public Task<string?> FindGroupBySessionAsync(string sessionId, CancellationToken cancellationToken)
        => LookupAsync("SESSION#" + sessionId, cancellationToken);
    public Task<string?> FindGroupByInviteAsync(string token, CancellationToken cancellationToken)
        => LookupAsync("INVITE#" + token, cancellationToken);

    public async Task<bool> SaveAsync(GroupAggregate group, long expectedVersion, string? removedSessionId, CancellationToken cancellationToken)
    {
        var writes = new List<TransactWriteItem>
        {
            new() { Put = new Put
            {
                TableName = table,
                Item = new()
                {
                    ["PK"] = new() { S = "GROUP#" + group.Id },
                    ["version"] = new() { N = group.Version.ToString(CultureInfo.InvariantCulture) },
                    ["data"] = new() { S = JsonSerializer.Serialize(group) }
                },
                ConditionExpression = "#version = :expected",
                ExpressionAttributeNames = new() { ["#version"] = "version" },
                ExpressionAttributeValues = new() { [":expected"] = new() { N = expectedVersion.ToString(CultureInfo.InvariantCulture) } }
            } },
            new() { ConditionCheck = new ConditionCheck
            {
                TableName = table,
                Key = new() { ["PK"] = new() { S = "RESERVATION#" + group.ReservationId } },
                ConditionExpression = "groupId = :group",
                ExpressionAttributeValues = new() { [":group"] = new() { S = group.Id } }
            } }
        };
        if (removedSessionId is not null)
        {
            writes.Add(new() { Delete = new Delete
            {
                TableName = table,
                Key = new() { ["PK"] = new() { S = "SESSION#" + removedSessionId } },
                ConditionExpression = "groupId = :group",
                ExpressionAttributeValues = new() { [":group"] = new() { S = group.Id } }
            } });
        }
        // Index new guest orders in the very same transaction as the aggregate.
        // Removed guests are deliberately excluded so later writes cannot recreate their access.
        foreach (var guestId in (group.GuestOrders?.Keys ?? Enumerable.Empty<string>())
            .Where(id => group.Participants.Any(person => person.Id == id)))
        {
            writes.Add(new() { Put = new Put
            {
                TableName = table,
                Item = new()
                {
                    ["PK"] = new() { S = "SESSION#" + guestId },
                    ["groupId"] = new() { S = group.Id }
                },
                ConditionExpression = "attribute_not_exists(PK) OR groupId = :group",
                ExpressionAttributeValues = new() { [":group"] = new() { S = group.Id } }
            } });
        }
        try
        {
            await client.TransactWriteItemsAsync(new TransactWriteItemsRequest
            {
                TransactItems = writes, ClientRequestToken = Guid.NewGuid().ToString("N")
            }, cancellationToken);
            return true;
        }
        catch (TransactionCanceledException exception) when (
            exception.CancellationReasons?.Any(reason => reason.Code is "ConditionalCheckFailed" or "TransactionConflict") == true)
        {
            return false;
        }
    }
}
