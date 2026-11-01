using System.Text.Json;
using Amazon;
using Amazon.Lambda.Core;
using Amazon.SimpleEmailV2;
using Amazon.SimpleEmailV2.Model;
using Microsoft.Extensions.Configuration;

namespace Restaurant.Api;

public sealed class NotificationAlarmMailer(IAmazonSimpleEmailServiceV2 client, IConfiguration settings)
{
    public async Task SendAsync(CancellationToken ct)
    {
        var address = settings["Email:Sender"];
        if (string.IsNullOrWhiteSpace(address)) throw new InvalidOperationException("Alert sender is not configured.");
        await client.SendEmailAsync(new SendEmailRequest
        {
            FromEmailAddress = address, Destination = new Destination { ToAddresses = [address] },
            Content = new EmailContent { Simple = new Message
            {
                Subject = new Content { Charset = "UTF-8", Data = "Green & Tasty: помилка надсилання сповіщень" },
                Body = new Body { Text = new Content { Charset = "UTF-8", Data =
                    "Фоновий обробник сповіщень повідомив про помилку. Система повторює надсилання в межах налаштованого часу. Перевірте CloudWatch та журнал сповіщень. Якщо повтори вичерпано, дані для відновлення зберігаються в захищеному сховищі протягом семи днів." } }
            } }
        }, ct);
    }
}

public sealed class NotificationAlarmLambda
{
    private readonly NotificationAlarmMailer mailer;
    public NotificationAlarmLambda()
    {
        var config = new ConfigurationBuilder().AddEnvironmentVariables().Build();
        var region = RegionEndpoint.GetBySystemName(config["AWS:Region"] ?? "eu-north-1");
        mailer = new(new AmazonSimpleEmailServiceV2Client(region), config);
    }

    [LambdaSerializer(typeof(Amazon.Lambda.Serialization.SystemTextJson.DefaultLambdaJsonSerializer))]
    public async Task HandleAsync(JsonElement input, ILambdaContext context)
    {
        if (!input.TryGetProperty("detail", out var detail) || !detail.TryGetProperty("state", out var state)
            || !state.TryGetProperty("value", out var value) || value.GetString() != "ALARM") return;
        try { await mailer.SendAsync(CancellationToken.None); }
        catch (Exception) { throw new InvalidOperationException("Notification alarm email failed."); }
    }
}
