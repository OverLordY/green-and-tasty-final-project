using Amazon;
using Amazon.Runtime;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Microsoft.Extensions.Configuration;
using Restaurant.Api;
using Xunit;

namespace Restaurant.Api.Tests;

public sealed class DynamoRepositoryTests
{
    private sealed class RecordingClient : AmazonDynamoDBClient
    {
        public RecordingClient() : base(new AnonymousAWSCredentials(), RegionEndpoint.EUNorth1) { }
        public TransactWriteItemsRequest? LastWrite { get; private set; }
        public bool FailCondition { get; set; }
        public override Task<TransactWriteItemsResponse> TransactWriteItemsAsync(TransactWriteItemsRequest request, CancellationToken cancellationToken = default)
        {
            LastWrite = request;
            if (FailCondition) throw new TransactionCanceledException("conflict")
            {
                CancellationReasons = [new CancellationReason { Code = "ConditionalCheckFailed" }]
            };
            return Task.FromResult(new TransactWriteItemsResponse());
        }
    }

    private static DynamoGroupRepository Create(RecordingClient client)
        => new(client, new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["DynamoDb:TableName"] = "test-table" }).Build());

    [Fact]
    public async Task CancellationConditionallyUpdatesScheduleAndGroupTogether()
    {
        using var client = new RecordingClient();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["DynamoDb:TableName"] = "test-table" }).Build();
        var start = DateTimeOffset.UtcNow.AddHours(2); var end = start.AddHours(1);
        var group = DevelopmentFixture.Create() with { TableId = "table-7", Version = 2, Status = "Closed", ReservationCancelled = true,
            Invite = DevelopmentFixture.Create().Invite with { StartTime = start.ToString("O"), EndTime = end.ToString("O") } };
        var writer = new DynamoBookingWriter(client, configuration);
        Assert.True(await writer.TryCancelAsync(group, new(3, [new("table-7", start, end)])));
        Assert.Equal(2, client.LastWrite!.TransactItems.Count);
        Assert.All(client.LastWrite.TransactItems, item => Assert.Equal("#version = :expected", item.Put.ConditionExpression));
        client.FailCondition = true;
        Assert.False(await writer.TryCancelAsync(group, new(3, [new("table-7", start, end)])));
    }

    [Fact]
    public async Task RescheduleUpdatesGroupAndScheduleInOneVersionCheckedTransaction()
    {
        using var client = new RecordingClient();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["DynamoDb:TableName"] = "test-table" }).Build();
        var start = DateTimeOffset.UtcNow.AddHours(2); var end = start.AddHours(1);
        var previous = DevelopmentFixture.Create() with { TableId = "table-7", Version = 2,
            Invite = DevelopmentFixture.Create().Invite with { StartTime = start.ToString("O"), EndTime = end.ToString("O") } };
        var updated = previous with { Version = 3, Invite = previous.Invite with { StartTime = end.ToString("O"), EndTime = end.AddHours(1).ToString("O") } };
        var writer = new DynamoBookingWriter(client, configuration);
        Assert.True(await writer.TryRescheduleAsync(previous, updated, new(3, [new("table-7", start, end)])));
        Assert.Equal(2, client.LastWrite!.TransactItems.Count);
        Assert.All(client.LastWrite.TransactItems, item => Assert.Equal("#version = :expected", item.Put.ConditionExpression));
        client.FailCondition = true;
        Assert.False(await writer.TryRescheduleAsync(previous, updated, new(3, [new("table-7", start, end)])));
    }

    [Fact]
    public async Task BookingCreatesScheduleGroupAndIndexesInOneConditionalTransaction()
    {
        using var client = new RecordingClient();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["DynamoDb:TableName"] = "test-table" }).Build();
        var writer = new DynamoBookingWriter(client, configuration);
        var now = DateTimeOffset.UtcNow;
        var request = new CreateBookingRequest("table-7", now.AddHours(2), now.AddHours(3), 2);
        var table = new BookingTable("table-7", "workshop-location", 7, 4);
        Assert.True(await writer.TryCreateAsync(DevelopmentFixture.Create(), request, table, new TableSchedule(0, []), now));
        var writes = client.LastWrite!.TransactItems;
        Assert.Equal(7, writes.Count);
        Assert.All(writes.Take(5), item => Assert.Equal("attribute_not_exists(PK)", item.Put.ConditionExpression));
        Assert.Contains("list_append", writes[5].Update.UpdateExpression);
        Assert.Contains("list_append", writes[6].Update.UpdateExpression);
        Assert.Contains(writes, item => item.Put.Item["PK"].S == "TABLE-SCHEDULE#table-7");
        Assert.Contains(writes, item => item.Put.Item["PK"].S == "RESERVATION#workshop-reservation");
        client.FailCondition = true;
        Assert.False(await writer.TryCreateAsync(DevelopmentFixture.Create(), request, table, new TableSchedule(2, []), now));
        Assert.Equal("#version = :expected", client.LastWrite!.TransactItems[0].Put.ConditionExpression);
        Assert.Equal("2", client.LastWrite.TransactItems[0].Put.ExpressionAttributeValues[":expected"].N);
    }

    [Fact]
    public async Task OverlappingBookingIsRejectedBeforeAnyDatabaseWrite()
    {
        using var client = new RecordingClient();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["DynamoDb:TableName"] = "test-table" }).Build();
        var now = DateTimeOffset.UtcNow; var request = new CreateBookingRequest("table-7", now.AddHours(2), now.AddHours(3), 2);
        var schedule = new TableSchedule(1, [new("table-7", now.AddHours(1), now.AddHours(2.5))]);
        var error = await Assert.ThrowsAsync<ApiException>(() => new DynamoBookingWriter(client, configuration).TryCreateAsync(
            DevelopmentFixture.Create(), request, new("table-7", "workshop-location", 7, 4), schedule, now));
        Assert.Equal(409, error.Status); Assert.Null(client.LastWrite);
    }

    [Fact]
    public async Task GroupAndReservationConditionAreInOneTransaction()
    {
        using var client = new RecordingClient();
        var fixture = DevelopmentFixture.Create() with { Version = 1, Status = "Submitted", OrderStatus = "SubmittedByCustomer" };
        Assert.True(await Create(client).SaveAsync(fixture, 0, null, default));
        Assert.Equal(2, client.LastWrite!.TransactItems.Count);
        Assert.Equal("#version = :expected", client.LastWrite.TransactItems[0].Put.ConditionExpression);
        Assert.Equal("0", client.LastWrite.TransactItems[0].Put.ExpressionAttributeValues[":expected"].N);
        Assert.Contains("SubmittedByCustomer", client.LastWrite.TransactItems[0].Put.Item["data"].S);
        Assert.Equal("RESERVATION#workshop-reservation", client.LastWrite.TransactItems[1].ConditionCheck.Key["PK"].S);
    }

    [Fact]
    public async Task DeleteSessionLookupSharesTheAtomicTransaction()
    {
        using var client = new RecordingClient();
        Assert.True(await Create(client).SaveAsync(DevelopmentFixture.Create() with { Version = 1 }, 0, "session-to-delete", default));
        Assert.Equal(3, client.LastWrite!.TransactItems.Count);
        Assert.Equal("SESSION#session-to-delete", client.LastWrite.TransactItems[2].Delete.Key["PK"].S);
        Assert.Equal("groupId = :group", client.LastWrite.TransactItems[2].Delete.ConditionExpression);
    }

    [Fact]
    public async Task GuestStateOwnerProjectionAndSessionIndexShareOneTransaction()
    {
        using var client = new RecordingClient();
        var flow = new GuestOrderWorkflow(DateTimeOffset.UtcNow.AddHours(1), TimeSpan.FromMinutes(15));
        var guest = new StoredGuestOrder("new-guest", flow.SaveState(), 0, "workshop-group", "Guest", "g@example.test");
        var group = DevelopmentFixture.Create();
        group = group with
        {
            Version = 1,
            GuestOrders = new() { [guest.Id] = guest },
            Participants = [.. group.Participants, new Participant(guest.Id, "Guest", "Guest", false, [])]
        };
        Assert.True(await Create(client).SaveAsync(group, 0, null, default));
        Assert.Equal(3, client.LastWrite!.TransactItems.Count);
        Assert.Contains("new-guest", client.LastWrite.TransactItems[0].Put.Item["data"].S);
        Assert.Equal("SESSION#new-guest", client.LastWrite.TransactItems[2].Put.Item["PK"].S);
        Assert.Contains("attribute_not_exists", client.LastWrite.TransactItems[2].Put.ConditionExpression);
    }

    [Fact]
    public async Task ConditionalFailureIsReportedAsConflict()
    {
        using var client = new RecordingClient { FailCondition = true };
        Assert.False(await Create(client).SaveAsync(DevelopmentFixture.Create() with { Version = 1 }, 0, null, default));
    }
}
