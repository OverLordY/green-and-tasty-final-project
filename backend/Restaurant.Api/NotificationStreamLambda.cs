using System.Text.Json;
using Amazon;
using Amazon.DynamoDBv2;
using Amazon.Lambda.Core;
using Amazon.SimpleEmailV2;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Restaurant.Api;

public sealed class NotificationStreamLambda
{
    private readonly NotificationStreamRecordHandler handler;

    public NotificationStreamLambda()
    {
        var config = new ConfigurationBuilder().AddEnvironmentVariables().Build();
        var region = RegionEndpoint.GetBySystemName(config["AWS:Region"] ?? "eu-north-1");
        var groups = new DynamoGroupRepository(new AmazonDynamoDBClient(region), config);
        var loggerFactory = LoggerFactory.Create(builder => builder.AddConsole());
        var mailer = new SesGuestConfirmationMailer(new AmazonSimpleEmailServiceV2Client(region), config,
            loggerFactory.CreateLogger<SesGuestConfirmationMailer>());
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
        catch (Exception exception)
        {
            // Do not let SDK error messages leak personal details into Lambda logs.
            context.Logger.LogLine($"Notification failure type: {exception.GetType().Name}");
            throw new InvalidOperationException("Notification stream processing failed; retry required.");
        }
    }
}
