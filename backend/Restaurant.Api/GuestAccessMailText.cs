using System.Globalization;
using System.Text;

namespace Restaurant.Api;

public static class GuestAccessMailText
{
    public static string Format(GuestAccessMail mail, IConfiguration settings)
    {
        if (!Uri.TryCreate(settings["Email:FrontendBaseUrl"], UriKind.Absolute, out var frontend) || frontend.Scheme != "https")
            throw new GuestMailUnavailableException();
        var body = new StringBuilder("Поверніться до вашого замовлення Green & Tasty\n\n");
        body.AppendLine(mail.RestaurantName); body.AppendLine("Зустріч: " + mail.ReservationStart);
        body.AppendLine("Відкриття посилання не підтверджує страви. На сторінці натисніть «Відновити доступ».");
        foreach (var link in mail.Links)
        {
            body.AppendLine("Замовлення: " + link.GuestName);
            body.AppendLine(new Uri(frontend, "/guest-confirmation") + "#order=" + Uri.EscapeDataString(link.OrderId)
                + "&recovery=" + Uri.EscapeDataString(link.Token));
            body.AppendLine("Посилання дійсне до: " + link.ExpiresAt.ToString("u", CultureInfo.InvariantCulture));
        }
        body.AppendLine("Кожне посилання одноразове. Після відновлення попередній персональний доступ буде відкликано.");
        body.AppendLine("Якщо ви не запитували доступ, проігноруйте цей лист. Ваше замовлення не зміниться.");
        return body.ToString();
    }
}
