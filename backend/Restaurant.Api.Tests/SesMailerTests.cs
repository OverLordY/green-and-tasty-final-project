using Amazon;
using Amazon.Runtime;
using Amazon.SimpleEmailV2;
using Amazon.SimpleEmailV2.Model;
using Microsoft.Extensions.Configuration;
using Restaurant.Api;
using Xunit;

namespace Restaurant.Api.Tests;

public sealed class SesMailerTests
{
    private sealed class RecordingClient() : AmazonSimpleEmailServiceV2Client(new AnonymousAWSCredentials(), RegionEndpoint.EUNorth1)
    {
        public SendEmailRequest? Request { get; private set; }
        public bool Reject { get; set; }
        public override Task<SendEmailResponse> SendEmailAsync(SendEmailRequest request, CancellationToken cancellationToken = default)
        {
            Request = request;
            if (Reject) throw new MessageRejectedException("test rejection");
            return Task.FromResult(new SendEmailResponse { MessageId = "test" });
        }
    }
    private static IConfiguration Settings() => new ConfigurationBuilder().AddInMemoryCollection(
        new Dictionary<string, string?> { ["Email:Sender"] = "sender@example.test", ["Email:FrontendBaseUrl"] = "https://frontend.example.test" }).Build();
    private static GuestConfirmationMail Mail() => new("guest@example.test", "Andrii", "group", "order",
        "test-one-time-token", DateTimeOffset.UtcNow.AddMinutes(15), [new("line", "salad", "Salad", "Olena", 12m, 2)]);

    [Fact]
    public async Task OperationalAlarmGoesOnlyToTheConfiguredSenderWithoutPersonalOrderData()
    {
        using var client = new RecordingClient();
        await new NotificationAlarmMailer(client, Settings()).SendAsync(default);
        Assert.Equal("sender@example.test", Assert.Single(client.Request!.Destination.ToAddresses));
        Assert.Contains("CloudWatch", client.Request.Content.Simple.Body.Text.Data);
        Assert.DoesNotContain("token", client.Request.Content.Simple.Body.Text.Data, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SendsUtf8ReceiptOnlyToTheOrderOwnerWithTokenInUrlFragment()
    {
        using var client = new RecordingClient();
        await new SesGuestConfirmationMailer(client, Settings()).SendAsync(Mail(), default);
        var request = Assert.IsType<SendEmailRequest>(client.Request);
        Assert.Equal("guest@example.test", Assert.Single(request.Destination.ToAddresses));
        Assert.Equal("UTF-8", request.Content.Simple.Body.Text.Charset);
        Assert.Contains("Salad × 2", request.Content.Simple.Body.Text.Data);
        Assert.Contains("Для: Olena", request.Content.Simple.Body.Text.Data);
        Assert.Contains("/guest-confirmation#order=order&token=test-one-time-token", request.Content.Simple.Body.Text.Data);
    }
    [Fact]
    public async Task OrganizerNotificationContainsTheChangesButNoGuestConfirmationLink()
    {
        using var client = new RecordingClient();
        var mailer = new SesGuestConfirmationMailer(client, Settings());
        await mailer.SendAsync(new OrderChangeMail("owner@example.test", "Організатор змінив Salad: 2 → 3", Mail().Items, true), default);
        var request = client.Request!;
        Assert.Equal("owner@example.test", Assert.Single(request.Destination.ToAddresses));
        Assert.Contains("Salad × 2", request.Content.Simple.Body.Text.Data);
        Assert.Contains("ще не включені", request.Content.Simple.Body.Text.Data);
        Assert.DoesNotContain("token=", request.Content.Simple.Body.Text.Data);
        Assert.DoesNotContain("guest-confirmation#", request.Content.Simple.Body.Text.Data);
    }

    [Fact]
    public async Task GuestChangeNotificationContainsExactVersionAndExplicitConfirmationLink()
    {
        using var client = new RecordingClient();
        await new SesGuestConfirmationMailer(client, Settings()).SendAsync(
            new OrderChangeMail("guest@example.test", "Організатор змінив замовлення", Mail().Items, true, Mail()), default);
        var body = client.Request!.Content.Simple.Body.Text.Data;
        Assert.Contains("Для: Olena", body); Assert.Contains("Разом: 24.00", body);
        Assert.Contains("#order=order&token=test-one-time-token", body);
    }

    [Fact]
    public async Task RecoveryMailIsAddressedOnlyToGuestAndUsesASeparateFragmentToken()
    {
        using var client = new RecordingClient();
        await new SesGuestConfirmationMailer(client, Settings()).SendAsync(new GuestAccessMail("guest@example.test", "Green & Tasty", "2030-01-01T19:00:00Z",
            [new("order", "Andrii", "test-recovery-token", DateTimeOffset.UtcNow.AddMinutes(15))]), default);
        var request = client.Request!;
        Assert.Equal("guest@example.test", Assert.Single(request.Destination.ToAddresses));
        Assert.Contains("#order=order&recovery=test-recovery-token", request.Content.Simple.Body.Text.Data);
        Assert.Contains("не підтверджує страви", request.Content.Simple.Body.Text.Data);
        Assert.DoesNotContain("?token=", request.Content.Simple.Body.Text.Data);
    }

    [Fact]
    public async Task ReportsRejectedDeliveryWithoutReturningSensitiveProviderDetails()
    {
        using var client = new RecordingClient { Reject = true };
        await Assert.ThrowsAsync<GuestMailUnavailableException>(() => new SesGuestConfirmationMailer(client, Settings()).SendAsync(Mail(), default));
    }
}
