using System.Globalization;
using System.Net;
using System.Net.Mail;
using System.Text;

namespace Restaurant.Api;

public sealed class GuestMailUnavailableException : Exception { }
public sealed class SmtpGuestConfirmationMailer(IConfiguration configuration) : IGuestConfirmationMailer, IOrderChangeMailer, IGuestAccessMailer
{
    public async Task SendAsync(GuestAccessMail message, CancellationToken cancellationToken)
    {
        var host = configuration["Email:SmtpHost"];
        var sender = configuration["Email:Sender"];
        if (string.IsNullOrWhiteSpace(host) || string.IsNullOrWhiteSpace(sender)) throw new GuestMailUnavailableException();
        using var email = new MailMessage(sender, message.To, "Відновлення доступу Green & Tasty", GuestAccessMailText.Format(message, configuration));
        using var smtp = new SmtpClient(host, configuration.GetValue<int?>("Email:SmtpPort") ?? 587) { EnableSsl = true };
        var username = configuration["Email:SmtpUsername"];
        if (!string.IsNullOrWhiteSpace(username)) smtp.Credentials = new NetworkCredential(username, configuration["Email:SmtpPassword"]);
        try { await smtp.SendMailAsync(email, cancellationToken); }
        catch (SmtpException) { throw new GuestMailUnavailableException(); }
    }

    public async Task SendAsync(OrderChangeMail message, CancellationToken cancellationToken)
    {
        var host = configuration["Email:SmtpHost"];
        var sender = configuration["Email:Sender"];
        if (string.IsNullOrWhiteSpace(host) || string.IsNullOrWhiteSpace(sender)) throw new GuestMailUnavailableException();
        using var email = new MailMessage(sender, message.To, "Організатор змінив замовлення Green & Tasty", OrderChangeMailText.Format(message, configuration));
        using var smtp = new SmtpClient(host, configuration.GetValue<int?>("Email:SmtpPort") ?? 587) { EnableSsl = true };
        var username = configuration["Email:SmtpUsername"];
        if (!string.IsNullOrWhiteSpace(username)) smtp.Credentials = new NetworkCredential(username, configuration["Email:SmtpPassword"]);
        try { await smtp.SendMailAsync(email, cancellationToken); }
        catch (SmtpException) { throw new GuestMailUnavailableException(); }
    }

    public async Task SendAsync(GuestConfirmationMail message, CancellationToken cancellationToken)
    {
        var host = configuration["Email:SmtpHost"];
        var sender = configuration["Email:Sender"];
        var baseUrl = configuration["Email:FrontendBaseUrl"];
        if (string.IsNullOrWhiteSpace(host) || string.IsNullOrWhiteSpace(sender)
            || !Uri.TryCreate(baseUrl, UriKind.Absolute, out var frontend) || frontend.Scheme != "https")
            throw new GuestMailUnavailableException();
        var link = new Uri(frontend, "/guest-confirmation").ToString() + "#order=" +
            Uri.EscapeDataString(message.OrderId) + "&token=" + Uri.EscapeDataString(message.ConfirmationToken);
        var body = new StringBuilder("Підтвердіть ваше замовлення\n\n");
        body.AppendLine(message.GuestName);
        foreach (var item in message.Items)
            body.AppendLine($"{item.Name} × {item.Quantity} · Для: {item.ForWhom} · {(item.UnitPrice * item.Quantity).ToString("0.00", CultureInfo.InvariantCulture)}");
        body.AppendLine("Разом: " + message.Items.Sum(item => item.UnitPrice * item.Quantity).ToString("0.00", CultureInfo.InvariantCulture));
        body.AppendLine("Дійсне до: " + message.ExpiresAt.ToString("u", CultureInfo.InvariantCulture));
        body.AppendLine(link);
        body.AppendLine("Якщо ви не оформлювали це замовлення, проігноруйте лист.");
        using var email = new MailMessage(sender, message.To, "Підтвердження замовлення Green & Tasty", body.ToString());
        using var smtp = new SmtpClient(host, configuration.GetValue<int?>("Email:SmtpPort") ?? 587) { EnableSsl = true };
        var username = configuration["Email:SmtpUsername"];
        if (!string.IsNullOrWhiteSpace(username)) smtp.Credentials = new NetworkCredential(username, configuration["Email:SmtpPassword"]);
        try { await smtp.SendMailAsync(email, cancellationToken); }
        catch (SmtpException) { throw new GuestMailUnavailableException(); }
    }
}
