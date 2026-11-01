using System.Text.Json;
using Amazon;
using Amazon.DynamoDBv2;
using Amazon.Lambda.Core;
using Amazon.SimpleEmailV2;
using Microsoft.Extensions.Configuration;

namespace Restaurant.Api;

public sealed class NotificationStreamLambda
{
    private readonly NotificationStreamRecordHandler handler;

    public NotificationStreamLambda()
    {
        var config = new ConfigurationBuilder().AddEnvironmentVariables().Build();
        var region = RegionEndpoint.GetBySystemName(config["AWS:Region"] ?? "eu-north-1");
        var groups = new DynamoGroupRepository(new AmazonDynamoDBClient(region), config);
        var mailer = new SesGuestConfirmationMailer(new AmazonSimpleEmailServiceV2Client(region), config);
        handler = new(new(groups, new(groups, mailer, TimeProvider.System), new(groups, mailer, mailer, TimeProvider.System)));
    }

    [LambdaSerializer(typeof(Amazon.Lambda.Serialization.SystemTextJson.DefaultLambdaJsonSerializer))]
    public async Task HandleAsync(JsonElement input, ILambdaContext context)
    {
        // Reserve time to fail the invocation, letting the stream retry safely.
        using var timeout = new CancellationTokenSource(context.RemainingTime > TimeSpan.FromSeconds(2)
            ? context.RemainingTime - TimeSpan.FromSeconds(2) : TimeSpan.Zero);
        try
        {
            foreach (var record in input.GetProperty("Records").EnumerateArray())
                await handler.ProcessAsync(record, timeout.Token);
        }
        catch (Exception)
        {
            // Do not let SDK error messages leak personal details into Lambda logs.
            throw new InvalidOperationException("Notification stream processing failed; retry required.");
        }
    }
}
