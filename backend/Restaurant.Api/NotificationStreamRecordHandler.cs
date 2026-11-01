using System.Text.Json;

namespace Restaurant.Api;

// Handles a single DynamoDB Streams record without retaining or logging its images.
public sealed class NotificationStreamRecordHandler(NotificationStreamProcessor processor)
{
    public async Task ProcessAsync(JsonElement record, CancellationToken ct = default)
    {
        if (!record.TryGetProperty("dynamodb", out var dynamodb) ||
            !dynamodb.TryGetProperty("Keys", out var keys) ||
            !keys.TryGetProperty("PK", out var key) || !key.TryGetProperty("S", out var keyString))
            throw new InvalidOperationException("Stream record is missing its key.");
        var pk = keyString.GetString();
        if (pk is null || !pk.StartsWith("GROUP#", StringComparison.Ordinal)) return;
        if (record.TryGetProperty("eventName", out var operation) && operation.GetString() == "REMOVE") return;
        var current = ReadImage(dynamodb, "NewImage") ?? throw new InvalidOperationException("Group stream record is missing its new image.");
        if (!string.Equals(pk, "GROUP#" + current.Id, StringComparison.Ordinal))
            throw new InvalidOperationException("Group stream key does not match its image.");
        var previous = ReadImage(dynamodb, "OldImage");
        if (operation.ValueKind == JsonValueKind.String && operation.GetString() == "MODIFY" && previous is null)
            throw new InvalidOperationException("Group modification is missing its old image.");
        await processor.ProcessAsync(previous, current, ct);
    }

    private static GroupAggregate? ReadImage(JsonElement dynamodb, string name)
    {
        if (!dynamodb.TryGetProperty(name, out var image)) return null;
        if (!image.TryGetProperty("data", out var data) || !data.TryGetProperty("S", out var json))
            throw new InvalidOperationException("Group stream image is missing its data.");
        return JsonSerializer.Deserialize<GroupAggregate>(json.GetString() ?? "")
            ?? throw new InvalidOperationException("Group stream image is invalid.");
    }
}
