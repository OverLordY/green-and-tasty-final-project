using Microsoft.Extensions.Configuration;
using Restaurant.Api;
using Xunit;

namespace Restaurant.Api.Tests;

public sealed class GroupGuestIntegrationTests
{
    private static readonly DateTimeOffset Now = new(2030, 1, 1, 17, 0, 0, TimeSpan.Zero);
    private sealed class Mailer : IGuestConfirmationMailer
    {
        public GuestConfirmationMail? Message { get; private set; }
        public Task SendAsync(GuestConfirmationMail message, CancellationToken cancellationToken)
        { Message = message; return Task.CompletedTask; }
    }
    private sealed class Clock : TimeProvider { public override DateTimeOffset GetUtcNow() => Now; }
    private static readonly GuestOrderLine Salad = new("salad:Olena", "salad", "Salad", "Olena", 12m, 2);
    private static GroupAggregate Group() => DevelopmentFixture.Create() with
    {
        Participants = [new("workshop-owner-session", "Owner", "Owner", false, [])]
    };
    private static IConfiguration Config() => new ConfigurationBuilder().Build();
    private sealed class InterleavingRepository(IGroupRepository inner) : IGroupRepository
    {
        public Func<Task>? BeforeNextSave { get; set; }
        public Task<GroupAggregate?> GetGroupAsync(string id, CancellationToken cancellationToken) => inner.GetGroupAsync(id, cancellationToken);
        public Task<string?> FindGroupByReservationAsync(string id, CancellationToken cancellationToken) => inner.FindGroupByReservationAsync(id, cancellationToken);
        public Task<string?> FindGroupBySessionAsync(string id, CancellationToken cancellationToken) => inner.FindGroupBySessionAsync(id, cancellationToken);
        public Task<string?> FindGroupByInviteAsync(string id, CancellationToken cancellationToken) => inner.FindGroupByInviteAsync(id, cancellationToken);
        public async Task<bool> SaveAsync(GroupAggregate group, long expectedVersion, string? removedSessionId, CancellationToken cancellationToken)
        {
            var action = BeforeNextSave; BeforeNextSave = null;
            if (action is not null) await action();
            return await inner.SaveAsync(group, expectedVersion, removedSessionId, cancellationToken);
        }
    }

    [Fact]
    public async Task SubmissionWinningTheRaceRejectsConcurrentGuestEditWithoutPartialWrite()
    {
        var groups = new InterleavingRepository(new InMemoryGroupRepository([Group()]));
        var mail = new Mailer();
        var guest = new GuestOrderService(new GroupGuestOrderStore(groups, Config()), mail, groups);
        var owner = new GroupService(groups, new Clock());
        var draft = await guest.CreateDraftAsync("workshop-group", "A", "a@example.test", Now.AddMinutes(30), Now, TimeSpan.FromMinutes(15));
        await guest.ProposeAsync(draft.OrderId, draft.DraftAccessToken, Salad, Now);
        await guest.RequestConfirmationAsync(draft.OrderId, draft.DraftAccessToken, Now);
        var access = (await guest.ConfirmAsync(draft.OrderId, mail.Message!.ConfirmationToken, Now)).GuestAccessToken!;
        groups.BeforeNextSave = async () => { await owner.SubmitAsync("workshop-group", "workshop-reservation", "workshop-owner-sub"); };
        await Assert.ThrowsAsync<InvalidOperationException>(() => guest.ProposeAsync(draft.OrderId, access, Salad with { Quantity = 3 }, Now));
        var saved = (await groups.GetGroupAsync("workshop-group", default))!;
        Assert.Equal("Submitted", saved.Status);
        Assert.Equal(2, Assert.Single(saved.OrderItems).Quantity);
        Assert.Null(saved.GuestOrders![draft.OrderId].State.Pending);
    }

    [Fact]
    public async Task OwnerSeesConfirmedVersionWhileIncreaseIsPendingAndRestaurantReceivesOnlyThatVersion()
    {
        var groups = new InMemoryGroupRepository([Group()]);
        var store = new GroupGuestOrderStore(groups, Config());
        var mail = new Mailer(); var guest = new GuestOrderService(store, mail, groups);
        var owner = new GroupService(groups, new Clock());
        var draft = await guest.CreateDraftAsync("workshop-group", "Andrii", "a@example.test",
            Now.AddMinutes(30), Now, TimeSpan.FromMinutes(15));
        await guest.ProposeAsync(draft.OrderId, draft.DraftAccessToken, Salad, Now);
        var before = await owner.GetSessionsAsync("workshop-group", "workshop-owner-sub");
        Assert.False(before.Participants.Single(p => p.Id == draft.OrderId).IsSubmitted);
        await guest.RequestConfirmationAsync(draft.OrderId, draft.DraftAccessToken, Now);
        var access = (await guest.ConfirmAsync(draft.OrderId, mail.Message!.ConfirmationToken, Now)).GuestAccessToken!;
        await guest.ProposeAsync(draft.OrderId, access, Salad with { Quantity = 3 }, Now);
        var snapshot = await owner.GetSessionsAsync("workshop-group", "workshop-owner-sub");
        var person = snapshot.Participants.Single(p => p.Id == draft.OrderId);
        Assert.True(person.IsSubmitted);
        Assert.Equal(2, Assert.Single(person.Items).Quantity);
        Assert.Equal(3, Assert.Single(person.PendingItems!).Quantity);
        Assert.Equal("Olena", Assert.Single(person.Items).ForWhom);
        Assert.Equal(24m, person.Total);
        var submitted = await owner.SubmitAsync("workshop-group", "workshop-reservation", "workshop-owner-sub");
        Assert.Equal(2, Assert.Single(submitted).Quantity);
        await Assert.ThrowsAsync<InvalidOperationException>(() => guest.ReduceAsync(draft.OrderId, access, Salad.Key, 1, Now));
    }

    [Fact]
    public async Task OwnerRemovalRevokesAccessAndExcludesGuestFromTotals()
    {
        var groups = new InMemoryGroupRepository([Group()]);
        var guest = new GuestOrderService(new GroupGuestOrderStore(groups, Config()), new Mailer(), groups);
        var owner = new GroupService(groups, new Clock());
        var draft = await guest.CreateDraftAsync("workshop-group", "A", "a@example.test", Now.AddMinutes(30), Now, TimeSpan.FromMinutes(15));
        await guest.ProposeAsync(draft.OrderId, draft.DraftAccessToken, Salad, Now);
        var removed = await owner.RemoveAsync("workshop-group", draft.OrderId, "workshop-owner-sub");
        Assert.DoesNotContain(removed.Participants, p => p.Id == draft.OrderId);
        Assert.Equal(0, removed.Total);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => guest.GetAsync(draft.OrderId, draft.DraftAccessToken));
    }

    [Fact]
    public async Task DeadlineBoundaryShowsOverdueToOwnerAndGuestButNotAfterSubmission()
    {
        var groups = new InMemoryGroupRepository([Group()]); var clock = new Clock();
        var guest = new GuestOrderService(new GroupGuestOrderStore(groups, Config()), new Mailer(), groups, clock);
        var draft = await guest.CreateDraftAsync("workshop-group", "Guest", "guest@example.test", Now.AddMinutes(30), Now, TimeSpan.FromMinutes(15));
        var group = (await groups.GetGroupAsync("workshop-group", default))!;
        Assert.True(await groups.SaveAsync(group with { SubmissionDeadline = Now, Version = group.Version + 1 }, group.Version, null, default));
        var owner = new GroupService(groups, clock);
        Assert.True((await owner.GetStatusAsync("workshop-group", "workshop-owner-sub")).SubmissionOverdue);
        Assert.True((await guest.GetAsync(draft.OrderId, draft.DraftAccessToken)).SubmissionOverdue);
        Assert.Equal(410, (await Assert.ThrowsAsync<ApiException>(() => owner.SubmitAsync("workshop-group", "workshop-reservation", "workshop-owner-sub"))).Status);
        group = (await groups.GetGroupAsync("workshop-group", default))!;
        Assert.True(await groups.SaveAsync(group with { Status = "Submitted", Version = group.Version + 1 }, group.Version, null, default));
        Assert.False((await guest.GetAsync(draft.OrderId, draft.DraftAccessToken)).SubmissionOverdue);
    }

    [Fact]
    public async Task SubmitAfterRestaurantDeadlineIsRejected()
    {
        var group = Group() with { SubmissionDeadline = Now };
        var owner = new GroupService(new InMemoryGroupRepository([group]), new Clock());
        var error = await Assert.ThrowsAsync<ApiException>(() => owner.SubmitAsync(group.Id, group.ReservationId, group.OwnerSubject));
        Assert.Equal(410, error.Status);
    }
}
