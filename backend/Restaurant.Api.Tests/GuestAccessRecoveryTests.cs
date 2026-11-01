using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Restaurant.Api;
using Xunit;

namespace Restaurant.Api.Tests;

public sealed class GuestAccessRecoveryTests
{
    private static readonly DateTimeOffset Now = new(2030, 1, 1, 17, 0, 0, TimeSpan.Zero);
    private static readonly GuestOrderLine Salad = new("salad:Olena", "salad", "Salad", "Olena", 12m, 2);
    private sealed class Clock : TimeProvider { public DateTimeOffset Current = Now; public override DateTimeOffset GetUtcNow() => Current; }
    private sealed class Mail : IGuestConfirmationMailer, IGuestAccessMailer
    {
        public GuestConfirmationMail? Confirmation;
        public List<GuestAccessMail> Access { get; } = [];
        public bool Fail;
        public Task SendAsync(GuestConfirmationMail mail, CancellationToken ct) { Confirmation = mail; return Task.CompletedTask; }
        public Task SendAsync(GuestAccessMail mail, CancellationToken ct)
        { if (Fail) throw new GuestMailUnavailableException(); Access.Add(mail); return Task.CompletedTask; }
    }
    private sealed class InterleavingRepository(IGroupRepository inner) : IGroupRepository
    {
        public Func<Task>? BeforeNextSave;
        public Task<GroupAggregate?> GetGroupAsync(string id, CancellationToken ct) => inner.GetGroupAsync(id, ct);
        public Task<string?> FindGroupByReservationAsync(string id, CancellationToken ct) => inner.FindGroupByReservationAsync(id, ct);
        public Task<string?> FindGroupBySessionAsync(string id, CancellationToken ct) => inner.FindGroupBySessionAsync(id, ct);
        public Task<string?> FindGroupByInviteAsync(string id, CancellationToken ct) => inner.FindGroupByInviteAsync(id, ct);
        public async Task<bool> SaveAsync(GroupAggregate group, long version, string? removed, CancellationToken ct)
        {
            var action = BeforeNextSave; BeforeNextSave = null;
            if (action is not null) await action();
            return await inner.SaveAsync(group, version, removed, ct);
        }
    }
    private sealed record Context(InterleavingRepository Groups, GuestOrderService Guest, GuestAccessRecoveryService Recovery,
        Mail Mail, Clock Clock, GuestDraft Draft);
    private static async Task<Context> Setup()
    {
        var group = DevelopmentFixture.Create() with { Participants = [new("workshop-owner-session", "Owner", "Owner", false, [])] };
        var groups = new InterleavingRepository(new InMemoryGroupRepository([group]));
        var clock = new Clock(); var mail = new Mail();
        var guest = new GuestOrderService(new GroupGuestOrderStore(groups, new ConfigurationBuilder().Build()), mail, groups, clock);
        var draft = await guest.CreateDraftAsync(group.Id, "Andrii", "guest@example.test", Now.AddMinutes(30), Now, TimeSpan.FromMinutes(15));
        await guest.ProposeAsync(draft.OrderId, draft.DraftAccessToken, Salad, Now);
        return new(groups, guest, new(groups, mail, clock), mail, clock, draft);
    }
    private static async Task<string> Confirm(Context c)
    {
        await c.Guest.RequestConfirmationAsync(c.Draft.OrderId, c.Draft.DraftAccessToken, Now);
        return (await c.Guest.ConfirmAsync(c.Draft.OrderId, c.Mail.Confirmation!.ConfirmationToken, Now)).GuestAccessToken!;
    }
    private static GuestAccessLink Link(Context c) => Assert.Single(Assert.Single(c.Mail.Access).Links);

    [Fact]
    public async Task RecoveryRestoresOnlyAccessAndNeverConfirmsPendingDishes()
    {
        var c = await Setup();
        await c.Recovery.RequestForOrderAsync(c.Draft.OrderId, "guest@example.test");
        var receipt = await c.Recovery.RedeemAsync(c.Draft.OrderId, Link(c).Token);
        var view = await c.Guest.GetAsync(c.Draft.OrderId, receipt.GuestAccessToken!);
        Assert.Empty(view.Confirmed); Assert.Single(view.Pending!); Assert.Equal(0, view.ConfirmedTotal);
        Assert.True(view.CanChange);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => c.Guest.GetAsync(c.Draft.OrderId, c.Draft.DraftAccessToken));
        var snapshot = (await c.Groups.GetGroupAsync("workshop-group", default))!.Snapshot();
        Assert.False(snapshot.Participants.Single(p => p.Id == c.Draft.OrderId).IsSubmitted);
    }

    [Fact]
    public async Task ConfirmedOrderKeepsAmountsButOldPersonalAccessAndConfirmationLinkAreRevoked()
    {
        var c = await Setup(); var oldAccess = await Confirm(c);
        await c.Guest.ProposeAsync(c.Draft.OrderId, oldAccess, Salad with { Quantity = 3 }, Now.AddMinutes(1));
        await c.Guest.RequestConfirmationAsync(c.Draft.OrderId, oldAccess, Now.AddMinutes(1));
        var oldConfirmation = c.Mail.Confirmation!.ConfirmationToken;
        await c.Recovery.RequestForOrderAsync(c.Draft.OrderId, "guest@example.test");
        var access = (await c.Recovery.RedeemAsync(c.Draft.OrderId, Link(c).Token)).GuestAccessToken!;
        var view = await c.Guest.GetAsync(c.Draft.OrderId, access);
        Assert.Equal(24m, view.ConfirmedTotal); Assert.Equal(3, Assert.Single(view.Pending!).Quantity);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => c.Guest.GetAsync(c.Draft.OrderId, oldAccess));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => c.Guest.ConfirmAsync(c.Draft.OrderId, oldConfirmation, Now));
    }

    [Fact]
    public async Task RecoveryRequestDoesNotRevokeAnExistingSessionUntilRedeemed()
    {
        var c = await Setup(); var old = await Confirm(c);
        await c.Recovery.RequestForOrderAsync(c.Draft.OrderId, "guest@example.test");
        Assert.Equal(24m, (await c.Guest.GetAsync(c.Draft.OrderId, old)).ConfirmedTotal);
    }

    [Fact]
    public async Task UnknownOrWrongEmailOrInviteDoesNotSendMailOrModifyAnOrder()
    {
        var c = await Setup(); var before = (await c.Groups.GetGroupAsync("workshop-group", default))!;
        await c.Recovery.RequestForOrderAsync(c.Draft.OrderId, "other@example.test");
        await c.Recovery.RequestForOrderAsync("unknown", "guest@example.test");
        await c.Recovery.RequestForInviteAsync("unknown", "guest@example.test");
        Assert.Empty(c.Mail.Access); Assert.Equal(before.Version, (await c.Groups.GetGroupAsync("workshop-group", default))!.Version);
    }

    [Fact]
    public async Task InviteRecoveryGroupsOnlyActiveOrdersWithMatchingEmailIntoOneMail()
    {
        var c = await Setup();
        var second = await c.Guest.CreateDraftAsync("workshop-group", "Second", "guest@example.test", Now.AddMinutes(30), Now, TimeSpan.FromMinutes(15));
        await c.Guest.CreateDraftAsync("workshop-group", "Other", "other@example.test", Now.AddMinutes(30), Now, TimeSpan.FromMinutes(15));
        await c.Recovery.RequestForInviteAsync("workshop-public-invite", "  GUEST@example.test  ");
        var mail = Assert.Single(c.Mail.Access);
        Assert.Equal(2, mail.Links.Length); Assert.Contains(mail.Links, l => l.OrderId == second.OrderId);
        var access = (await c.Recovery.RedeemAsync(c.Draft.OrderId, mail.Links.Single(l => l.OrderId == c.Draft.OrderId).Token)).GuestAccessToken!;
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => c.Guest.GetAsync(second.OrderId, access));
    }

    [Fact]
    public async Task RecoveryTokensArePerOrderOneTimeAndStoredOnlyAsDigests()
    {
        var c = await Setup();
        var second = await c.Guest.CreateDraftAsync("workshop-group", "Other", "guest@example.test", Now.AddMinutes(30), Now, TimeSpan.FromMinutes(15));
        await c.Recovery.RequestForOrderAsync(c.Draft.OrderId, "guest@example.test");
        var token = Link(c).Token;
        Assert.DoesNotContain(token, JsonSerializer.Serialize(await c.Groups.GetGroupAsync("workshop-group", default)));
        Assert.Equal(401, (await Assert.ThrowsAsync<GuestAccessLinkException>(() => c.Recovery.RedeemAsync(second.OrderId, token))).Status);
        var receipt = await c.Recovery.RedeemAsync(c.Draft.OrderId, token);
        Assert.Equal(401, (await Assert.ThrowsAsync<GuestAccessLinkException>(() => c.Recovery.RedeemAsync(c.Draft.OrderId, token))).Status);
        Assert.DoesNotContain(receipt.GuestAccessToken!, JsonSerializer.Serialize(await c.Groups.GetGroupAsync("workshop-group", default)));
    }

    [Fact]
    public async Task ExpiryBoundaryAndCooldownAndResendInvalidateTheOldRecoveryLink()
    {
        var c = await Setup(); await c.Recovery.RequestForOrderAsync(c.Draft.OrderId, "guest@example.test");
        var token = Link(c).Token;
        c.Clock.Current = Now.AddSeconds(59); await c.Recovery.RequestForOrderAsync(c.Draft.OrderId, "guest@example.test");
        Assert.Single(c.Mail.Access);
        c.Clock.Current = Now.AddMinutes(15);
        Assert.Equal("access_link_expired", (await Assert.ThrowsAsync<GuestAccessLinkException>(() => c.Recovery.RedeemAsync(c.Draft.OrderId, token))).Code);
        await c.Recovery.RequestForOrderAsync(c.Draft.OrderId, "guest@example.test"); Assert.Equal(2, c.Mail.Access.Count);
        Assert.Equal(401, (await Assert.ThrowsAsync<GuestAccessLinkException>(() => c.Recovery.RedeemAsync(c.Draft.OrderId, token))).Status);
        await c.Recovery.RedeemAsync(c.Draft.OrderId, c.Mail.Access.Last().Links[0].Token);
    }

    [Fact]
    public async Task AfterSubmissionRecoveryAllowsReadButCannotChangeOrConfirmDishes()
    {
        var c = await Setup(); await Confirm(c);
        await new GroupService(c.Groups, c.Clock).SubmitAsync("workshop-group", "workshop-reservation", "workshop-owner-sub");
        await c.Recovery.RequestForOrderAsync(c.Draft.OrderId, "guest@example.test");
        var access = (await c.Recovery.RedeemAsync(c.Draft.OrderId, Link(c).Token)).GuestAccessToken!;
        var view = await c.Guest.GetAsync(c.Draft.OrderId, access);
        Assert.Equal(24m, view.ConfirmedTotal); Assert.False(view.CanChange);
        await Assert.ThrowsAsync<InvalidOperationException>(() => c.Guest.ProposeAsync(c.Draft.OrderId, access, Salad with { Quantity = 3 }, Now));
        var group = (await c.Groups.GetGroupAsync("workshop-group", default))!;
        Assert.Equal("Submitted", group.Status); Assert.Equal(2, Assert.Single(group.OrderItems).Quantity);
    }

    [Fact]
    public async Task RecoveringAfterEditDeadlineDoesNotReopenEditing()
    {
        var c = await Setup(); c.Clock.Current = Now.AddHours(1);
        await c.Recovery.RequestForOrderAsync(c.Draft.OrderId, "guest@example.test");
        Assert.Equal(c.Clock.Current.AddMinutes(15), Link(c).ExpiresAt);
        var access = (await c.Recovery.RedeemAsync(c.Draft.OrderId, Link(c).Token)).GuestAccessToken!;
        Assert.False((await c.Guest.GetAsync(c.Draft.OrderId, access)).CanChange);
        await Assert.ThrowsAsync<InvalidOperationException>(() => c.Guest.ProposeAsync(c.Draft.OrderId, access, Salad, c.Clock.Current));
    }

    [Fact]
    public async Task RemovalRevokesOutstandingRecoveryLinkAndPreventsFurtherRecoveryMail()
    {
        var c = await Setup(); await c.Recovery.RequestForOrderAsync(c.Draft.OrderId, "guest@example.test"); var token = Link(c).Token;
        await new GroupService(c.Groups, c.Clock).RemoveAsync("workshop-group", c.Draft.OrderId, "workshop-owner-sub");
        Assert.Equal(401, (await Assert.ThrowsAsync<GuestAccessLinkException>(() => c.Recovery.RedeemAsync(c.Draft.OrderId, token))).Status);
        c.Clock.Current = Now.AddMinutes(1); await c.Recovery.RequestForInviteAsync("workshop-public-invite", "guest@example.test");
        Assert.Single(c.Mail.Access);
    }

    [Fact]
    public async Task OnlyOneConcurrentRedemptionCanSucceed()
    {
        var c = await Setup(); await c.Recovery.RequestForOrderAsync(c.Draft.OrderId, "guest@example.test"); var token = Link(c).Token;
        ConfirmationReceipt? winner = null;
        c.Groups.BeforeNextSave = async () => { winner = await c.Recovery.RedeemAsync(c.Draft.OrderId, token); };
        Assert.Equal(409, (await Assert.ThrowsAsync<ApiException>(() => c.Recovery.RedeemAsync(c.Draft.OrderId, token))).Status);
        await c.Guest.GetAsync(c.Draft.OrderId, winner!.GuestAccessToken!);
    }

    [Fact]
    public async Task RecoveryRequestRetriesAfterConcurrentSubmissionWithoutReopeningTheGroup()
    {
        var c = await Setup(); await Confirm(c);
        c.Groups.BeforeNextSave = async () => { await new GroupService(c.Groups, c.Clock).SubmitAsync("workshop-group", "workshop-reservation", "workshop-owner-sub"); };
        await c.Recovery.RequestForOrderAsync(c.Draft.OrderId, "guest@example.test");
        Assert.Single(c.Mail.Access); Assert.Equal("Submitted", (await c.Groups.GetGroupAsync("workshop-group", default))!.Status);
        await c.Recovery.RedeemAsync(c.Draft.OrderId, Link(c).Token);
    }

    [Fact]
    public async Task MailProviderFailureIsNotExposedAndRequestCanBeRetriedAfterCooldown()
    {
        var c = await Setup(); c.Mail.Fail = true;
        await c.Recovery.RequestForOrderAsync(c.Draft.OrderId, "guest@example.test"); Assert.Empty(c.Mail.Access);
        c.Mail.Fail = false; c.Clock.Current = Now.AddMinutes(1);
        await c.Recovery.RequestForOrderAsync(c.Draft.OrderId, "guest@example.test"); Assert.Single(c.Mail.Access);
    }
}
