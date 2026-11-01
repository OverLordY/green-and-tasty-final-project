using System.Globalization;
using System.Text;

namespace Restaurant.Api;

public static class OrderChangeMailText
{
    public static string Format(OrderChangeMail message, IConfiguration settings)
    {
        var body = new StringBuilder(message.Summary + "\n\nСклад замовлення після цієї зміни:\n");
        foreach (var line in message.Items)
            body.AppendLine($"{line.Name} × {line.Quantity} · Для: {line.ForWhom} · {(line.UnitPrice * line.Quantity).ToString("0.00", CultureInfo.InvariantCulture)}");
        if (message.Items.Length == 0) body.AppendLine("Страв немає.");
        body.AppendLine("Разом: " + message.Items.Sum(l => l.UnitPrice * l.Quantity).ToString("0.00", CultureInfo.InvariantCulture));
        if (message.Superseded) body.AppendLine("Після цієї дії замовлення було змінено або закрито. Цей лист є повідомленням про попередню зміну.");
        else if (message.NeedsConfirmation) body.AppendLine("Додані порції очікують підтвердження гостя і ще не включені до замовлення ресторану.");
        else if (message.IsReservationChange) body.AppendLine("Підтверджені страви залишаються чинними. Непідтверджені зміни не входять до замовлення ресторану.");
        else body.AppendLine("Зменшення та скасування вже набули чинності.");
        if (message.Confirmation is { } confirmation)
        {
            if (!Uri.TryCreate(settings["Email:FrontendBaseUrl"], UriKind.Absolute, out var url) || url.Scheme != "https")
                throw new GuestMailUnavailableException();
            body.AppendLine("Для підтвердження відкрийте посилання і натисніть кнопку:");
            body.AppendLine(new Uri(url, "/guest-confirmation") + "#order=" + Uri.EscapeDataString(confirmation.OrderId)
                + "&token=" + Uri.EscapeDataString(confirmation.ConfirmationToken));
            body.AppendLine("Дійсне до: " + confirmation.ExpiresAt.ToString("u", CultureInfo.InvariantCulture));
        }
        return body.ToString();
    }
}
