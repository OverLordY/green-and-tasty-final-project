using System.Globalization;
using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;

namespace Restaurant.Api;

public sealed record TableSchedule(long Version, BookingInterval[] Intervals);

// One versioned schedule per table serializes arbitrary overlapping intervals.
// The group and all lookup keys are created in the same DynamoDB transaction.
public sealed class DynamoBookingWriter(IAmazonDynamoDB client, IConfiguration configuration) : IBookingWriter, IOwnerBookingReader, IBookingCancellation, ILocationBookingReader, IBookingRescheduler
{
    private readonly string table = configuration["DynamoDb:TableName"] ?? throw new InvalidOperationException("DynamoDb:TableName is required.");
    private static string ScheduleKey(string tableId) => "TABLE-SCHEDULE#" + tableId;
    private static string OwnerKey(string subject) => "OWNER#" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(subject)));
    private static string LocationKey(string id) => "LOCATION#" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(id)));
    public async Task<bool> TryRescheduleAsync(GroupAggregate previous, GroupAggregate updated, TableSchedule schedule, CancellationToken ct = default)
    {
        if (previous.TableId is null || updated.TableId != previous.TableId || updated.Id != previous.Id || updated.Version != previous.Version + 1 || schedule.Version < 1) return false;
        var start = DateTimeOffset.Parse(previous.Invite.StartTime, CultureInfo.InvariantCulture);
        var end = DateTimeOffset.Parse(previous.Invite.EndTime, CultureInfo.InvariantCulture);
        var others = schedule.Intervals.Where(item => !(item.TableId == previous.TableId && item.Start == start && item.End == end)).ToArray();
        if (others.Length != schedule.Intervals.Length - 1) return false;
        var newStart = DateTimeOffset.Parse(updated.Invite.StartTime, CultureInfo.InvariantCulture);
        var newEnd = DateTimeOffset.Parse(updated.Invite.EndTime, CultureInfo.InvariantCulture);
        if (others.Any(item => newStart < item.End && item.Start < newEnd)) return false;
        var next = new TableSchedule(checked(schedule.Version + 1), [.. others, new(previous.TableId, newStart, newEnd)]);
        Put Conditional(string key, long previousVersion, string data) => new()
        {
            TableName = table, Item = new() { ["PK"] = new() { S = key }, ["version"] = new() { N = (previousVersion + 1).ToString(CultureInfo.InvariantCulture) }, ["data"] = new() { S = data } },
            ConditionExpression = "#version = :expected", ExpressionAttributeNames = new() { ["#version"] = "version" },
            ExpressionAttributeValues = new() { [":expected"] = new() { N = previousVersion.ToString(CultureInfo.InvariantCulture) } }
        };
        try
        {
            await client.TransactWriteItemsAsync(new TransactWriteItemsRequest { TransactItems = [
                new() { Put = Conditional(ScheduleKey(previous.TableId), schedule.Version, JsonSerializer.Serialize(next)) },
                new() { Put = Conditional("GROUP#" + previous.Id, previous.Version, JsonSerializer.Serialize(updated)) }
            ] }, ct); return true;
        }
        catch (TransactionCanceledException error) when (error.CancellationReasons?.Any(reason => reason.Code is "ConditionalCheckFailed" or "TransactionConflict") == true) { return false; }
    }
    public async Task<GroupAggregate[]> ListLocationAsync(string locationId, CancellationToken ct = default)
    {
        var index = await client.GetItemAsync(new GetItemRequest { TableName = table, ConsistentRead = true,
            Key = new() { ["PK"] = new() { S = LocationKey(locationId) } } }, ct);
        var groups = new List<GroupAggregate>();
        foreach (var id in index.Item?.GetValueOrDefault("groupIds")?.L ?? [])
        {
            var item = await client.GetItemAsync(new GetItemRequest { TableName = table, ConsistentRead = true,
                Key = new() { ["PK"] = new() { S = "GROUP#" + id.S } } }, ct);
            if (item.Item is not { Count: > 0 }) continue;
            var group = JsonSerializer.Deserialize<GroupAggregate>(item.Item["data"].S);
            if (group?.Invite.LocationId == locationId && group.TableId is not null) groups.Add(group);
        }
        return groups.ToArray();
    }
    public async Task<bool> TryCancelAsync(GroupAggregate group, TableSchedule schedule, CancellationToken ct = default)
    {
        if (group.TableId is null || schedule.Version < 1 || group.Version < 1) return false;
        var start = DateTimeOffset.Parse(group.Invite.StartTime, CultureInfo.InvariantCulture);
        var end = DateTimeOffset.Parse(group.Invite.EndTime, CultureInfo.InvariantCulture);
        var intervals = schedule.Intervals.Where(item => !(item.TableId == group.TableId && item.Start == start && item.End == end)).ToArray();
        if (intervals.Length != schedule.Intervals.Length - 1) return false;
        var next = new TableSchedule(checked(schedule.Version + 1), intervals);
        Put Conditional(string key, long previous, Dictionary<string, AttributeValue> data) => new()
        {
            TableName = table, Item = new() { ["PK"] = new() { S = key }, ["version"] = new() { N = (previous + 1).ToString(CultureInfo.InvariantCulture) }, ["data"] = data["data"] },
            ConditionExpression = "#version = :expected", ExpressionAttributeNames = new() { ["#version"] = "version" },
            ExpressionAttributeValues = new() { [":expected"] = new() { N = previous.ToString(CultureInfo.InvariantCulture) } }
        };
        try
        {
            await client.TransactWriteItemsAsync(new TransactWriteItemsRequest { TransactItems = [
                new() { Put = Conditional(ScheduleKey(group.TableId), schedule.Version, new() { ["data"] = new() { S = JsonSerializer.Serialize(next) } }) },
                new() { Put = Conditional("GROUP#" + group.Id, group.Version - 1, new() { ["data"] = new() { S = JsonSerializer.Serialize(group) } }) }
            ] }, ct); return true;
        }
        catch (TransactionCanceledException error) when (error.CancellationReasons?.Any(reason => reason.Code is "ConditionalCheckFailed" or "TransactionConflict") == true) { return false; }
    }

    public async Task<GroupAggregate[]> ListAsync(string subject, CancellationToken ct = default)
    {
        var index = await client.GetItemAsync(new GetItemRequest { TableName = table, ConsistentRead = true,
            Key = new() { ["PK"] = new() { S = OwnerKey(subject) } } }, ct);
        var ids = index.Item?.GetValueOrDefault("groupIds")?.L ?? [];
        var groups = new List<GroupAggregate>();
        foreach (var id in ids)
        {
            var item = await client.GetItemAsync(new GetItemRequest { TableName = table, ConsistentRead = true,
                Key = new() { ["PK"] = new() { S = "GROUP#" + id.S } } }, ct);
            if (item.Item is not { Count: > 0 }) continue;
            var group = JsonSerializer.Deserialize<GroupAggregate>(item.Item["data"].S);
            if (group?.OwnerSubject == subject && group.TableId is not null) groups.Add(group);
        }
        return groups.ToArray();
    }

    public async Task<TableSchedule> ReadScheduleAsync(string tableId, CancellationToken ct = default)
    {
        var result = await client.GetItemAsync(new GetItemRequest { TableName = table, ConsistentRead = true,
            Key = new() { ["PK"] = new() { S = ScheduleKey(tableId) } } }, ct);
        return result.Item is { Count: > 0 } ? JsonSerializer.Deserialize<TableSchedule>(result.Item["data"].S)
            ?? throw new InvalidOperationException("Invalid table schedule.") : new(0, []);
    }

    public async Task<bool> TryCreateAsync(GroupAggregate group, CreateBookingRequest request, BookingTable bookingTable,
        TableSchedule schedule, DateTimeOffset now, CancellationToken ct = default)
    {
        BookingRules.Validate(request, bookingTable, schedule.Intervals, now);
        if (group.Version != 0 || group.Invite.LocationId != bookingTable.LocationId || group.Invite.TableNumber != bookingTable.Number)
            throw new ArgumentException("Group does not match the table.");
        var next = new TableSchedule(checked(schedule.Version + 1), [.. schedule.Intervals,
            new BookingInterval(request.TableId, request.StartTime, request.EndTime)]);
        Put Unique(string key, Dictionary<string, AttributeValue> values)
        {
            values["PK"] = new() { S = key };
            return new Put { TableName = table, Item = values, ConditionExpression = "attribute_not_exists(PK)" };
        }
        var scheduleWrite = new Put
        {
            TableName = table,
            Item = new() { ["PK"] = new() { S = ScheduleKey(request.TableId) },
                ["version"] = new() { N = next.Version.ToString(CultureInfo.InvariantCulture) }, ["data"] = new() { S = JsonSerializer.Serialize(next) } },
            ConditionExpression = schedule.Version == 0 ? "attribute_not_exists(PK)" : "#version = :expected",
            ExpressionAttributeNames = schedule.Version == 0 ? null : new() { ["#version"] = "version" },
            ExpressionAttributeValues = schedule.Version == 0 ? null : new() { [":expected"] = new() { N = schedule.Version.ToString(CultureInfo.InvariantCulture) } }
        };
        var writes = new List<TransactWriteItem>
        {
            new() { Put = scheduleWrite },
            new() { Put = Unique("GROUP#" + group.Id, new() { ["version"] = new() { N = "0" }, ["data"] = new() { S = JsonSerializer.Serialize(group) } }) },
            new() { Put = Unique("RESERVATION#" + group.ReservationId, new() { ["groupId"] = new() { S = group.Id } }) },
            new() { Put = Unique("SESSION#" + group.OwnerSessionId, new() { ["groupId"] = new() { S = group.Id } }) },
            new() { Put = Unique("INVITE#" + group.InviteToken, new() { ["groupId"] = new() { S = group.Id } }) }
        };
        writes.Add(new() { Update = new Update { TableName = table, Key = new() { ["PK"] = new() { S = OwnerKey(group.OwnerSubject) } },
            UpdateExpression = "SET groupIds = list_append(if_not_exists(groupIds, :empty), :group)",
            ExpressionAttributeValues = new() { [":empty"] = new() { L = [] }, [":group"] = new() { L = [new() { S = group.Id }] } } } });
        writes.Add(new() { Update = new Update { TableName = table, Key = new() { ["PK"] = new() { S = LocationKey(group.Invite.LocationId) } },
            UpdateExpression = "SET groupIds = list_append(if_not_exists(groupIds, :empty), :group)",
            ExpressionAttributeValues = new() { [":empty"] = new() { L = [] }, [":group"] = new() { L = [new() { S = group.Id }] } } } });
        try { await client.TransactWriteItemsAsync(new TransactWriteItemsRequest { TransactItems = writes }, ct); return true; }
        catch (TransactionCanceledException error) when (error.CancellationReasons?.Any(reason => reason.Code is "ConditionalCheckFailed" or "TransactionConflict") == true) { return false; }
    }
}
