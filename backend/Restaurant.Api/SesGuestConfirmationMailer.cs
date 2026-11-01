using System.Globalization;
using System.Text;
using Amazon.SimpleEmailV2;
using Amazon.SimpleEmailV2.Model;

namespace Restaurant.Api;

public sealed class SesGuestConfirmationMailer(IAmazonSimpleEmailServiceV2 client, IConfiguration settings, ILogger<SesGuestConfirmationMailer>? logger = null)
    : IGuestConfirmationMailer, IOrderChangeMailer, IGuestAccessMailer
{
    public async Task SendAsync(GuestAccessMail message, CancellationToken cancellationToken)
    {
        var sender = settings["Email:Sender"];
        if (string.IsNullOrWhiteSpace(sender)) throw new GuestMailUnavailableException();
        var request = new SendEmailRequest
        {
            FromEmailAddress = sender, Destination = new Destination { ToAddresses = [message.To] },
            Content = new EmailContent { Simple = new Message
            {
                Subject = new Content { Data = "Відновлення доступу Green & Tasty", Charset = "UTF-8" },
                Body = new Body { Text = new Content { Data = GuestAccessMailText.Format(message, settings), Charset = "UTF-8" } }
            } }
        };
        try { await client.SendEmailAsync(request, cancellationToken); }
        catch (AmazonSimpleEmailServiceV2Exception exception) { LogFailure(exception, sender, message.To); throw new GuestMailUnavailableException(); }
    }

    public async Task SendAsync(OrderChangeMail message, CancellationToken cancellationToken)
    {
        var sender = settings["Email:Sender"];
        if (string.IsNullOrWhiteSpace(sender)) throw new GuestMailUnavailableException();
        var request = new SendEmailRequest
        {
            FromEmailAddress = sender, Destination = new Destination { ToAddresses = [message.To] },
            Content = new EmailContent { Simple = new Message
            {
                Subject = new Content { Data = "Організатор змінив замовлення Green & Tasty", Charset = "UTF-8" },
                Body = new Body { Text = new Content { Data = OrderChangeMailText.Format(message, settings), Charset = "UTF-8" } }
            } }
        };
        try { await client.SendEmailAsync(request, cancellationToken); }
        catch (AmazonSimpleEmailServiceV2Exception exception) { LogFailure(exception, sender, message.To); throw new GuestMailUnavailableException(); }
    }

    public async Task SendAsync(GuestConfirmationMail message, CancellationToken cancellationToken)
    {
        var sender = settings["Email:Sender"];
        if (string.IsNullOrWhiteSpace(sender) || !Uri.TryCreate(settings["Email:FrontendBaseUrl"], UriKind.Absolute, out var frontend)
            || frontend.Scheme != "https") throw new GuestMailUnavailableException();
        var link = new Uri(frontend, "/guest-confirmation").ToString() + "#order=" +
            Uri.EscapeDataString(message.OrderId) + "&token=" + Uri.EscapeDataString(message.ConfirmationToken);
        var body = new StringBuilder("Підтвердіть ваше замовлення\n\n");
        body.AppendLine(message.GuestName);
        foreach (var line in message.Items)
            body.AppendLine($"{line.Name} × {line.Quantity} · Для: {line.ForWhom} · {(line.UnitPrice * line.Quantity).ToString("0.00", CultureInfo.InvariantCulture)}");
        body.AppendLine("Разом: " + message.Items.Sum(line => line.UnitPrice * line.Quantity).ToString("0.00", CultureInfo.InvariantCulture));
        body.AppendLine("Дійсне до: " + message.ExpiresAt.ToString("u", CultureInfo.InvariantCulture));
        body.AppendLine("Для підтвердження відкрийте посилання та натисніть кнопку:");
        body.AppendLine(link);
        body.AppendLine("Якщо ви не оформлювали це замовлення, проігноруйте лист.");
        var request = new SendEmailRequest
        {
            FromEmailAddress = sender,
            Destination = new Destination { ToAddresses = [message.To] },
            Content = new EmailContent
            {
                Simple = new Message
                {
                    Subject = new Content { Data = "Підтвердження замовлення Green & Tasty", Charset = "UTF-8" },
                    Body = new Body { Text = new Content { Data = body.ToString(), Charset = "UTF-8" } }
                }
            }
        };
        try { await client.SendEmailAsync(request, cancellationToken); }
        catch (AmazonSimpleEmailServiceV2Exception exception) { LogFailure(exception, sender, message.To); throw new GuestMailUnavailableException(); }
    }
    private void LogFailure(AmazonSimpleEmailServiceV2Exception error, string sender, string recipient)
    {
        var detail = error.Message ?? "";
        logger?.LogWarning("SES delivery failed: code {ErrorCode}, HTTP {StatusCode}; explicit deny {ExplicitDeny}, organization policy {OrganizationPolicy}, identity policy {IdentityPolicy}, sender referenced {SenderReferenced}, recipient referenced {RecipientReferenced}",
            error.ErrorCode, (int)error.StatusCode,
            detail.Contains("explicit deny", StringComparison.OrdinalIgnoreCase),
            detail.Contains("service control policy", StringComparison.OrdinalIgnoreCase),
            detail.Contains("identity-based policy", StringComparison.OrdinalIgnoreCase),
            detail.Contains(sender, StringComparison.OrdinalIgnoreCase),
            detail.Contains(recipient, StringComparison.OrdinalIgnoreCase));
    }
}
