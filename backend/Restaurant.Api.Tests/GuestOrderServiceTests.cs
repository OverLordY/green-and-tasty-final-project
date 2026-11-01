using System.Text.Json;
using Restaurant.Api;
using Xunit;

namespace Restaurant.Api.Tests;

public sealed class GuestOrderServiceTests
{
    private static readonly DateTimeOffset Now = new(2030, 1, 1, 17, 0, 0, TimeSpan.Zero);
    private static readonly GuestOrderLine Salad = new("salad-olena", "salad", "Salad", "Olena", 12m, 2);
    private sealed class RecordingMailer : IGuestConfirmationMailer
    {
        public GuestConfirmationMail? LastMessage { get; private set; }
        public bool Fail { get; set; }
        public Task SendAsync(GuestConfirmationMail message, CancellationToken cancellationToken)
        {
            if (Fail) throw new InvalidOperationException("Delivery failed.");
            LastMessage = message;
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task GuestFlowConfirmsOnlyTheMailedVersionAndRevokesDraftAccess()
    {
        var store = new InMemoryGuestOrderStore();
        var mailer = new RecordingMailer();
        var service = new GuestOrderService(store, mailer);
        var draft = await service.CreateDraftAsync("verified-group", "Andrii", "andrii@example.test",
            Now.AddMinutes(30), Now, TimeSpan.FromMinutes(15));
        await service.ProposeAsync(draft.OrderId, draft.DraftAccessToken, Salad, Now);
        await service.RequestConfirmationAsync(draft.OrderId, draft.DraftAccessToken, Now);
        var message = Assert.IsType<GuestConfirmationMail>(mailer.LastMessage);
        Assert.Equal("andrii@example.test", message.To);
        Assert.Equal("Olena", Assert.Single(message.Items).ForWhom);
        Assert.Equal(2, Assert.Single(message.Items).Quantity);
        Assert.Equal(0, GuestOrderWorkflow.Restore((await store.FindAsync(draft.OrderId, default))!.State).ConfirmedTotal);
        var receipt = await service.ConfirmAsync(draft.OrderId, message.ConfirmationToken, Now.AddMinutes(1));
        var access = Assert.IsType<string>(receipt.GuestAccessToken);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.ProposeAsync(
            draft.OrderId, draft.DraftAccessToken, Salad with { Quantity = 3 }, Now.AddMinutes(2)));
        var view = await service.ProposeAsync(draft.OrderId, access, Salad with { Quantity = 3 }, Now.AddMinutes(2));
        Assert.Equal(24m, view.ConfirmedTotal);
        Assert.Equal(3, Assert.Single(view.Pending!).Quantity);
        var stored = JsonSerializer.Serialize(await store.FindAsync(draft.OrderId, default));
        Assert.DoesNotContain(access, stored);
        Assert.DoesNotContain(message.ConfirmationToken, stored);
        Assert.DoesNotContain(draft.DraftAccessToken, stored);
    }

    [Fact]
    public async Task DifferentGuestCannotEditOrRequestEmailForAnotherOrder()
    {
        var store = new InMemoryGuestOrderStore(); var service = new GuestOrderService(store, new RecordingMailer());
        var first = await service.CreateDraftAsync("group", "A", "a@example.test", Now.AddMinutes(30), Now, TimeSpan.FromMinutes(15));
        var second = await service.CreateDraftAsync("group", "B", "b@example.test", Now.AddMinutes(30), Now, TimeSpan.FromMinutes(15));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.ProposeAsync(first.OrderId, second.DraftAccessToken, Salad, Now));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.RequestConfirmationAsync(first.OrderId, second.DraftAccessToken, Now));
        Assert.Empty(GuestOrderWorkflow.Restore((await store.FindAsync(first.OrderId, default))!.State).Pending!);
    }

    [Fact]
    public async Task ChangedCartInvalidatesPreviouslyMailedLink()
    {
        var mailer = new RecordingMailer(); var store = new InMemoryGuestOrderStore();
        var service = new GuestOrderService(store, mailer);
        var draft = await service.CreateDraftAsync("group", "A", "a@example.test", Now.AddMinutes(30), Now, TimeSpan.FromMinutes(15));
        await service.ProposeAsync(draft.OrderId, draft.DraftAccessToken, Salad, Now);
        await service.RequestConfirmationAsync(draft.OrderId, draft.DraftAccessToken, Now);
        var oldToken = mailer.LastMessage!.ConfirmationToken;
        await service.ProposeAsync(draft.OrderId, draft.DraftAccessToken, Salad with { Quantity = 3 }, Now.AddMinutes(1));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.ConfirmAsync(draft.OrderId, oldToken, Now.AddMinutes(2)));
        Assert.Equal(0, GuestOrderWorkflow.Restore((await store.FindAsync(draft.OrderId, default))!.State).ConfirmedTotal);
    }

    [Fact]
    public async Task FailedMailDeliveryIsReportedAndCanBeRetried()
    {
        var mailer = new RecordingMailer { Fail = true }; var store = new InMemoryGuestOrderStore();
        var service = new GuestOrderService(store, mailer);
        var draft = await service.CreateDraftAsync("group", "A", "a@example.test", Now.AddMinutes(30), Now, TimeSpan.FromMinutes(15));
        await service.ProposeAsync(draft.OrderId, draft.DraftAccessToken, Salad, Now);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.RequestConfirmationAsync(draft.OrderId, draft.DraftAccessToken, Now));
        Assert.Null(mailer.LastMessage);
        mailer.Fail = false;
        await service.RequestConfirmationAsync(draft.OrderId, draft.DraftAccessToken, Now.AddMinutes(1));
        Assert.NotNull(mailer.LastMessage);
    }

    [Fact]
    public async Task RepeatedEmailRequestsAreRateLimitedPerOrder()
    {
        var mailer = new RecordingMailer(); var service = new GuestOrderService(new InMemoryGuestOrderStore(), mailer);
        var draft = await service.CreateDraftAsync("group", "A", "a@example.test", Now.AddMinutes(30), Now, TimeSpan.FromMinutes(15));
        await service.ProposeAsync(draft.OrderId, draft.DraftAccessToken, Salad, Now);
        await service.RequestConfirmationAsync(draft.OrderId, draft.DraftAccessToken, Now);
        var first = mailer.LastMessage!.ConfirmationToken;
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.RequestConfirmationAsync(draft.OrderId, draft.DraftAccessToken, Now.AddSeconds(29)));
        await service.RequestConfirmationAsync(draft.OrderId, draft.DraftAccessToken, Now.AddSeconds(30));
        Assert.NotEqual(first, mailer.LastMessage!.ConfirmationToken);
    }

    [Theory]
    [InlineData("not an email")]
    [InlineData("Name <person@example.test>")]
    [InlineData("")]
    public async Task InvalidEmailIsRejected(string email)
    {
        var service = new GuestOrderService(new InMemoryGuestOrderStore(), new RecordingMailer());
        await Assert.ThrowsAsync<ArgumentException>(() => service.CreateDraftAsync(
            "group", "A", email, Now.AddMinutes(30), Now, TimeSpan.FromMinutes(15)));
    }
}
