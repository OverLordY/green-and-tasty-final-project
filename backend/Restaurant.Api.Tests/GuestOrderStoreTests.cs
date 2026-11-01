using System.Text.Json;
using Amazon;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.Runtime;
using Microsoft.Extensions.Configuration;
using Restaurant.Api;
using Xunit;

namespace Restaurant.Api.Tests;

public sealed class GuestOrderStoreTests
{
    private static readonly DateTimeOffset Now = new(2030, 1, 1, 17, 0, 0, TimeSpan.Zero);
    private static GuestOrderWorkflow Order() => new(Now.AddMinutes(30), TimeSpan.FromMinutes(15));

    [Fact]
    public async Task RestorePreservesCapabilityWithoutStoringRawToken()
    {
        var order = Order();
        order.Propose(new("line", "salad", "Salad", "Olena", 12m, 2), Now);
        var challenge = order.RequestConfirmation(Now);
        var access = Assert.IsType<string>(order.Confirm(challenge.Token, Now.AddMinutes(1)).GuestAccessToken);
        var store = new InMemoryGuestOrderStore();
        var record = new StoredGuestOrder("order-1", order.SaveState(), 0);
        Assert.True(await store.CreateAsync(record, default));
        var restored = GuestOrderWorkflow.Restore((await store.FindAsync("order-1", default))!.State);
        Assert.True(restored.IsGuestAuthorized(access));
        Assert.Equal(24m, restored.ConfirmedTotal);
        var saved = JsonSerializer.Serialize(record);
        Assert.DoesNotContain(access, saved);
        Assert.DoesNotContain(challenge.Token, saved);
    }

    [Fact]
    public async Task CompetingUpdatesRejectTheStaleVersion()
    {
        var store = new InMemoryGuestOrderStore();
        var record = new StoredGuestOrder("order-1", Order().SaveState(), 0);
        Assert.True(await store.CreateAsync(record, default));
        Assert.False(await store.CreateAsync(record, default));
        var first = (await store.FindAsync("order-1", default))!;
        var second = (await store.FindAsync("order-1", default))!;
        Assert.True(await store.UpdateAsync(first with { Version = 1 }, 0, default));
        Assert.False(await store.UpdateAsync(second with { Version = 1 }, 0, default));
        Assert.Equal(1, (await store.FindAsync("order-1", default))!.Version);
    }

    private sealed class RecordingClient : AmazonDynamoDBClient
    {
        public RecordingClient() : base(new AnonymousAWSCredentials(), RegionEndpoint.EUNorth1) { }
        public PutItemRequest? LastWrite { get; private set; }
        public bool Conflict { get; set; }
        public override Task<PutItemResponse> PutItemAsync(PutItemRequest request, CancellationToken cancellationToken = default)
        {
            LastWrite = request;
            if (Conflict) throw new ConditionalCheckFailedException("stale");
            return Task.FromResult(new PutItemResponse());
        }
    }

    [Fact]
    public async Task DynamoUpdatesUseExpectedVersionAndReportConflict()
    {
        using var client = new RecordingClient();
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["DynamoDb:TableName"] = "test-table" }).Build();
        var store = new DynamoGuestOrderStore(client, config);
        var order = new StoredGuestOrder("order-1", Order().SaveState(), 0);
        Assert.True(await store.CreateAsync(order, default));
        Assert.Equal("attribute_not_exists(PK)", client.LastWrite!.ConditionExpression);
        Assert.True(await store.UpdateAsync(order with { Version = 1 }, 0, default));
        Assert.Equal("#v = :expected", client.LastWrite!.ConditionExpression);
        Assert.Equal("0", client.LastWrite.ExpressionAttributeValues[":expected"].N);
        client.Conflict = true;
        Assert.False(await store.UpdateAsync(order with { Version = 1 }, 0, default));
    }
}
