using Microsoft.Extensions.Configuration;
using System.Text.Json;
using Restaurant.Api;
using Xunit;

namespace Restaurant.Api.Tests;

public sealed class OwnerGuestOrderTests
{
    private static readonly DateTimeOffset Now = new(2030, 1, 1, 17, 0, 0, TimeSpan.Zero);
    private sealed class Clock : TimeProvider { public override DateTimeOffset GetUtcNow() => Now; }
    private sealed class LaterClock : TimeProvider { public override DateTimeOffset GetUtcNow() => Now.AddHours(1); }
    private sealed class Mail : IGuestConfirmationMailer, IOrderChangeMailer, IGuestAccessMailer
    {
        public GuestConfirmationMail? Confirmation;
        public GuestAccessMail? Recovery;
        public List<OrderChangeMail> Changes { get; } = [];
        public string? FailTo;
        public Func<Task>? DuringSend;
        public Task SendAsync(GuestConfirmationMail message, CancellationToken ct)
        { if (FailTo == message.To) throw new GuestMailUnavailableException(); Confirmation = message; return Task.CompletedTask; }
        public Task SendAsync(GuestAccessMail message, CancellationToken ct)
        { if (FailTo == message.To) throw new GuestMailUnavailableException(); Recovery = message; return Task.CompletedTask; }
        public async Task SendAsync(OrderChangeMail message, CancellationToken ct)
        {
            if (FailTo == message.To) throw new GuestMailUnavailableException();
            Changes.Add(message);
            if (DuringSend is { } action) { DuringSend = null; await action(); }
        }
    }
    private sealed record Context(InMemoryGroupRepository Groups, GuestOrderService Guest, OwnerGuestOrderService Owner,
        OwnerNotificationService Notifications, Mail Mail, string Id, string Access);
    private static async Task<Context> Setup(bool ownerEmail = true)
    {
        var group = DevelopmentFixture.Create() with
        {
            OwnerEmail = ownerEmail ? "owner@example.test" : null,
            Participants = [new("workshop-owner-session", "Owner", "Owner", false, [])]
        };
        var groups = new InMemoryGroupRepository([group]);
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Restaurant:Menu:0:Id"] = "salad", ["Restaurant:Menu:0:Name"] = "Salad",
            ["Restaurant:Menu:0:Price"] = "12", ["Restaurant:Menu:0:Available"] = "true"
        }).Build();
        var mail = new Mail();
        var guest = new GuestOrderService(new GroupGuestOrderStore(groups, config), mail, groups);
        var draft = await guest.CreateDraftAsync(group.Id, "Guest", "guest@example.test", Now.AddMinutes(30), Now, TimeSpan.FromMinutes(15));
        await guest.ProposeAsync(draft.OrderId, draft.DraftAccessToken, new("salad:Olena", "salad", "Salad", "Olena", 12m, 2), Now);
        await guest.RequestConfirmationAsync(draft.OrderId, draft.DraftAccessToken, Now);
        var access = (await guest.ConfirmAsync(draft.OrderId, mail.Confirmation!.ConfirmationToken, Now)).GuestAccessToken!;
        return new(groups, guest, new(groups, config, new Clock()), new(groups, mail, new Clock()), mail, draft.OrderId, access);
    }
    private static Task<OwnerChangeResult> Change(Context c, int quantity, string subject = "workshop-owner-sub")
        => c.Owner.ChangeAsync("workshop-group", c.Id, subject, null, new("salad", "Olena", quantity));

    private static IConfiguration BackgroundConfig() => new ConfigurationBuilder().AddInMemoryCollection(
        new Dictionary<string, string?> { ["GuestMail:Background"] = "true" }).Build();

    [Fact]
    public async Task GuestConfirmationIsQueuedAtomicallyAndDeliveredByWorkerWithoutRawTokenStorage()
    {
        var c = await Setup();
        await c.Guest.ProposeAsync(c.Id, c.Access, new("salad:Olena", "salad", "Salad", "Olena", 12m, 3), Now);
        var before = (await c.Groups.GetGroupAsync("workshop-group", default))!;
        var service = new GuestOrderService(new GroupGuestOrderStore(c.Groups, BackgroundConfig()), c.Mail, c.Groups, new Clock(), BackgroundConfig());
        c.Mail.Confirmation = null;
        await service.RequestConfirmationAsync(c.Id, c.Access, Now.AddMinutes(1));
        var after = (await c.Groups.GetGroupAsync("workshop-group", default))!;
        Assert.NotNull(after.GuestOrders![c.Id].ConfirmationMail); Assert.Null(c.Mail.Confirmation);
        var processor = new NotificationStreamProcessor(c.Groups, c.Notifications, new(c.Groups, c.Mail, c.Mail, new Clock()));
        await processor.ProcessAsync(before, after);
        Assert.NotNull(c.Mail.Confirmation);
        var saved = (await c.Groups.GetGroupAsync("workshop-group", default))!;
        Assert.DoesNotContain(c.Mail.Confirmation!.ConfirmationToken, JsonSerializer.Serialize(saved));
        await c.Guest.ConfirmAsync(c.Id, c.Mail.Confirmation.ConfirmationToken, Now.AddMinutes(1));
        await processor.ProcessAsync(before, after);
    }

    [Fact]
    public async Task GuestRecoveryIsQueuedPrivatelyAndRetryIssuesWorkingLink()
    {
        var c = await Setup(); var before = (await c.Groups.GetGroupAsync("workshop-group", default))!;
        var recovery = new GuestAccessRecoveryService(c.Groups, c.Mail, new Clock(), BackgroundConfig());
        await recovery.RequestForOrderAsync(c.Id, "guest@example.test");
        var after = (await c.Groups.GetGroupAsync("workshop-group", default))!;
        Assert.NotNull(after.GuestOrders![c.Id].RecoveryMail); Assert.Null(c.Mail.Recovery);
        var processor = new NotificationStreamProcessor(c.Groups, c.Notifications, new(c.Groups, c.Mail, c.Mail, new Clock()));
        c.Mail.FailTo = "guest@example.test";
        await Assert.ThrowsAsync<NotificationDeliveryPendingException>(() => processor.ProcessAsync(before, after));
        c.Mail.FailTo = null;
        await processor.ProcessAsync(before, after);
        var token = Assert.Single(c.Mail.Recovery!.Links).Token;
        Assert.DoesNotContain(token, JsonSerializer.Serialize((await c.Groups.GetGroupAsync("workshop-group", default))!));
        Assert.NotNull((await recovery.RedeemAsync(c.Id, token)).GuestAccessToken);
    }

    [Fact]
    public async Task GuestConfirmationQueueDoesNotSendAnObsoleteOrderAfterFurtherEdits()
    {
        var c = await Setup();
        await c.Guest.ProposeAsync(c.Id, c.Access, new("salad:Olena", "salad", "Salad", "Olena", 12m, 3), Now);
        var before = (await c.Groups.GetGroupAsync("workshop-group", default))!;
        var service = new GuestOrderService(new GroupGuestOrderStore(c.Groups, BackgroundConfig()), c.Mail, c.Groups, new Clock(), BackgroundConfig());
        await service.RequestConfirmationAsync(c.Id, c.Access, Now.AddMinutes(1));
        var queued = (await c.Groups.GetGroupAsync("workshop-group", default))!;
        await c.Guest.ProposeAsync(c.Id, c.Access, new("salad:Olena", "salad", "Salad", "Olena", 12m, 4), Now.AddMinutes(1));
        c.Mail.Confirmation = null;
        await new NotificationStreamProcessor(c.Groups, c.Notifications, new(c.Groups, c.Mail, c.Mail, new Clock())).ProcessAsync(before, queued);
        Assert.Null(c.Mail.Confirmation);
    }

    [Fact]
    public async Task GuestConfirmationDeliveryRetriesAfterMailFailure()
    {
        var c = await Setup();
        await c.Guest.ProposeAsync(c.Id, c.Access, new("salad:Olena", "salad", "Salad", "Olena", 12m, 3), Now);
        var before = (await c.Groups.GetGroupAsync("workshop-group", default))!;
        var service = new GuestOrderService(new GroupGuestOrderStore(c.Groups, BackgroundConfig()), c.Mail, c.Groups, new Clock(), BackgroundConfig());
        await service.RequestConfirmationAsync(c.Id, c.Access, Now.AddMinutes(1));
        var after = (await c.Groups.GetGroupAsync("workshop-group", default))!;
        var processor = new NotificationStreamProcessor(c.Groups, c.Notifications, new(c.Groups, c.Mail, c.Mail, new Clock()));
        c.Mail.Confirmation = null; c.Mail.FailTo = "guest@example.test";
        await Assert.ThrowsAsync<NotificationDeliveryPendingException>(() => processor.ProcessAsync(before, after));
        Assert.Null(c.Mail.Confirmation);
        c.Mail.FailTo = null;
        await processor.ProcessAsync(before, after);
        var link = c.Mail.Confirmation!.ConfirmationToken;
        Assert.NotNull((await c.Guest.ConfirmAsync(c.Id, link, Now.AddMinutes(1))).GuestAccessToken);
    }

    [Fact]
    public async Task GuestMailWorkerDoesNotSendAfterItsDeadline()
    {
        var c = await Setup();
        await c.Guest.ProposeAsync(c.Id, c.Access, new("salad:Olena", "salad", "Salad", "Olena", 12m, 3), Now);
        var before = (await c.Groups.GetGroupAsync("workshop-group", default))!;
        var service = new GuestOrderService(new GroupGuestOrderStore(c.Groups, BackgroundConfig()), c.Mail, c.Groups, new Clock(), BackgroundConfig());
        await service.RequestConfirmationAsync(c.Id, c.Access, Now.AddMinutes(1));
        var after = (await c.Groups.GetGroupAsync("workshop-group", default))!;
        c.Mail.Confirmation = null;
        await new NotificationStreamProcessor(c.Groups, c.Notifications, new(c.Groups, c.Mail, c.Mail, new LaterClock())).ProcessAsync(before, after);
        Assert.Null(c.Mail.Confirmation);
    }

    [Fact]
    public async Task RecoveryQueueIgnoresUnknownEmailAndDoesNotExposeItsToken()
    {
        var c = await Setup(); var before = (await c.Groups.GetGroupAsync("workshop-group", default))!;
        var recovery = new GuestAccessRecoveryService(c.Groups, c.Mail, new Clock(), BackgroundConfig());
        await recovery.RequestForOrderAsync(c.Id, "unknown@example.test");
        var after = (await c.Groups.GetGroupAsync("workshop-group", default))!;
        Assert.Null(after.GuestOrders![c.Id].RecoveryMail); Assert.Equal(before.Version, after.Version);
        Assert.Null(c.Mail.Recovery);
    }

    [Fact]
    public async Task StreamProcessesNewIntentsAndIgnoresProgressWrites()
    {
        var c = await Setup();
        var before = (await c.Groups.GetGroupAsync("workshop-group", default))!;
        await Change(c, 1);
        var after = (await c.Groups.GetGroupAsync("workshop-group", default))!;
        var processor = new NotificationStreamProcessor(c.Groups, c.Notifications);
        await processor.ProcessAsync(before, after);
        Assert.Equal(2, c.Mail.Changes.Count);
        var progress = (await c.Groups.GetGroupAsync("workshop-group", default))!;
        await processor.ProcessAsync(after, progress);
        await processor.ProcessAsync(before, after); // duplicate stream record
        Assert.Equal(2, c.Mail.Changes.Count);
    }

    [Fact]
    public async Task StreamFailureRequestsRetryAndDoesNotResendAcceptedRecipient()
    {
        var c = await Setup();
        var before = (await c.Groups.GetGroupAsync("workshop-group", default))!;
        await Change(c, 3);
        var after = (await c.Groups.GetGroupAsync("workshop-group", default))!;
        var processor = new NotificationStreamProcessor(c.Groups, c.Notifications);
        c.Mail.FailTo = "owner@example.test";
        await Assert.ThrowsAsync<NotificationDeliveryPendingException>(() => processor.ProcessAsync(before, after));
        c.Mail.FailTo = null;
        await processor.ProcessAsync(before, after);
        Assert.Single(c.Mail.Changes, m => m.To == "guest@example.test");
        Assert.Single(c.Mail.Changes, m => m.To == "owner@example.test");
    }

    [Fact]
    public async Task StreamRetriesWhenBrowserAlreadyHoldsNotificationLease()
    {
        var c = await Setup();
        var before = (await c.Groups.GetGroupAsync("workshop-group", default))!;
        var change = await Change(c, 1);
        var after = (await c.Groups.GetGroupAsync("workshop-group", default))!;
        var processor = new NotificationStreamProcessor(c.Groups, c.Notifications);
        c.Mail.DuringSend = async () =>
            await Assert.ThrowsAsync<NotificationDeliveryPendingException>(() => processor.ProcessAsync(before, after));
        await c.Notifications.SendAsync("workshop-group", change.NotificationId, "workshop-owner-sub");
        await processor.ProcessAsync(before, after);
        Assert.Equal(2, c.Mail.Changes.Count);
    }

    [Fact]
    public async Task StreamWillNotProcessMismatchedGroupImages()
    {
        var c = await Setup();
        var group = (await c.Groups.GetGroupAsync("workshop-group", default))!;
        var processor = new NotificationStreamProcessor(c.Groups, c.Notifications);
        await Assert.ThrowsAsync<InvalidOperationException>(() => processor.ProcessAsync(group, group with { Id = "other" }));
        Assert.Empty(c.Mail.Changes);
    }

    [Fact]
    public async Task DynamoStreamRecordProcessesOnlyGroupChanges()
    {
        var c = await Setup();
        var before = (await c.Groups.GetGroupAsync("workshop-group", default))!;
        await Change(c, 1);
        var after = (await c.Groups.GetGroupAsync("workshop-group", default))!;
        var handler = new NotificationStreamRecordHandler(new(c.Groups, c.Notifications));
        var record = JsonSerializer.SerializeToElement(new { eventName = "MODIFY", dynamodb = new {
            Keys = new { PK = new { S = "GROUP#workshop-group" } },
            OldImage = new { data = new { S = JsonSerializer.Serialize(before) } },
            NewImage = new { data = new { S = JsonSerializer.Serialize(after) } }
        } });
        await handler.ProcessAsync(record);
        Assert.Equal(2, c.Mail.Changes.Count);
        var other = JsonSerializer.SerializeToElement(new { eventName = "MODIFY", dynamodb = new { Keys = new { PK = new { S = "SESSION#ignored" } } } });
        await handler.ProcessAsync(other);
        Assert.Equal(2, c.Mail.Changes.Count);
    }

    [Fact]
    public async Task DynamoStreamRejectsGroupKeyMismatch()
    {
        var c = await Setup(); var group = (await c.Groups.GetGroupAsync("workshop-group", default))!;
        var record = JsonSerializer.SerializeToElement(new { eventName = "MODIFY", dynamodb = new {
            Keys = new { PK = new { S = "GROUP#another" } }, NewImage = new { data = new { S = JsonSerializer.Serialize(group) } }
        } });
        await Assert.ThrowsAsync<InvalidOperationException>(() => new NotificationStreamRecordHandler(new(c.Groups, c.Notifications)).ProcessAsync(record));
    }

    [Fact]
    public async Task DynamoStreamRejectsModificationWithoutOldImage()
    {
        var c = await Setup(); var group = (await c.Groups.GetGroupAsync("workshop-group", default))!;
        var record = JsonSerializer.SerializeToElement(new { eventName = "MODIFY", dynamodb = new {
            Keys = new { PK = new { S = "GROUP#workshop-group" } }, NewImage = new { data = new { S = JsonSerializer.Serialize(group) } }
        } });
        await Assert.ThrowsAsync<InvalidOperationException>(() => new NotificationStreamRecordHandler(new(c.Groups, c.Notifications)).ProcessAsync(record));
        Assert.Empty(c.Mail.Changes);
    }

    [Fact]
    public async Task DecreaseIsImmediateAndAuditAndNotificationArePersistedTogether()
    {
        var c = await Setup(); var result = await Change(c, 1);
        var person = result.Snapshot.Participants.Single(p => p.Id == c.Id);
        Assert.Equal(12m, person.Total); Assert.Equal(1, Assert.Single(person.Items).Quantity);
        Assert.Contains("Організатор", person.OwnerChangeSummary);
        var stored = (await c.Groups.GetGroupAsync("workshop-group", default))!;
        Assert.Equal(c.Id, Assert.Single(stored.Audit!).GuestOrderId);
        Assert.Equal(result.NotificationId, Assert.Single(stored.Notifications!).Id);
        var view = await c.Guest.GetAsync(c.Id, c.Access);
        Assert.Equal(12m, view.ConfirmedTotal); Assert.Contains("зменшена до 1", Assert.Single(view.Notices).Message);
    }

    [Fact]
    public async Task IncreaseEmailsGuestConfirmationButNeverSendsCapabilityToOwner()
    {
        var c = await Setup(); var change = await Change(c, 3);
        Assert.Equal(24m, change.Snapshot.Participants.Single(p => p.Id == c.Id).Total);
        var sent = await c.Notifications.SendAsync("workshop-group", change.NotificationId, "workshop-owner-sub");
        Assert.True(sent.GuestAccepted); Assert.True(sent.OwnerAccepted);
        var guestMail = c.Mail.Changes.Single(m => m.To == "guest@example.test");
        var ownerMail = c.Mail.Changes.Single(m => m.To == "owner@example.test");
        Assert.NotNull(guestMail.Confirmation); Assert.Null(ownerMail.Confirmation);
        Assert.Equal(3, Assert.Single(guestMail.Items).Quantity);
        var access = (await c.Guest.ConfirmAsync(c.Id, guestMail.Confirmation!.ConfirmationToken, Now)).GuestAccessToken!;
        Assert.Equal(36m, (await c.Guest.GetAsync(c.Id, access)).ConfirmedTotal);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => c.Guest.GetAsync(c.Id, c.Access));
    }

    [Fact]
    public async Task RecipientLabelCannotBeUsedAsAnEmailAddressForANewLine()
    {
        var c = await Setup();
        var exception = await Assert.ThrowsAsync<ApiException>(() => c.Owner.ChangeAsync("workshop-group", c.Id,
            "workshop-owner-sub", null, new("salad", "other@example.test", 1)));
        Assert.Equal(400, exception.Status);
        Assert.Contains("name", exception.Message);
        var snapshot = (await c.Groups.GetGroupAsync("workshop-group", default))!.Snapshot();
        Assert.Equal("g***@example.test", snapshot.Participants.Single(p => p.Id == c.Id).ContactEmailMasked);
        Assert.Empty(c.Mail.Changes);
    }

    [Fact]
    public async Task PartialDeliveryCanRetryOnlyTheUnsentRecipient()
    {
        var c = await Setup(); var change = await Change(c, 1); c.Mail.FailTo = "owner@example.test";
        var first = await c.Notifications.SendAsync("workshop-group", change.NotificationId, "workshop-owner-sub");
        Assert.True(first.GuestAccepted); Assert.False(first.OwnerAccepted);
        c.Mail.FailTo = null;
        var second = await c.Notifications.SendAsync("workshop-group", change.NotificationId, "workshop-owner-sub");
        Assert.True(second.OwnerAccepted);
        Assert.Single(c.Mail.Changes, m => m.To == "guest@example.test");
        await c.Notifications.SendAsync("workshop-group", change.NotificationId, "workshop-owner-sub");
        Assert.Equal(2, c.Mail.Changes.Count);
    }

    [Fact]
    public async Task FailedGuestEmailLeavesDurableIntentAndRetryCreatesUsableConfirmation()
    {
        var c = await Setup(); var change = await Change(c, 3); c.Mail.FailTo = "guest@example.test";
        var first = await c.Notifications.SendAsync("workshop-group", change.NotificationId, "workshop-owner-sub");
        Assert.False(first.GuestAccepted);
        c.Mail.FailTo = null;
        await c.Notifications.SendAsync("workshop-group", change.NotificationId, "workshop-owner-sub");
        var token = c.Mail.Changes.Single(m => m.To == "guest@example.test").Confirmation!.ConfirmationToken;
        await c.Guest.ConfirmAsync(c.Id, token, Now);
    }

    [Fact]
    public async Task RetryOfSupersededIncreaseDoesNotConfirmAnUnrelatedNewerVersion()
    {
        var c = await Setup(); var first = await Change(c, 3); await Change(c, 4);
        await c.Notifications.SendAsync("workshop-group", first.NotificationId, "workshop-owner-sub");
        var mail = c.Mail.Changes.Single(m => m.To == "guest@example.test");
        Assert.True(mail.Superseded); Assert.Null(mail.Confirmation);
        Assert.Equal(24m, (await c.Guest.GetAsync(c.Id, c.Access)).ConfirmedTotal);
    }

    [Fact]
    public async Task ConcurrentGuestDecreaseDuringSendIsNeverOverwrittenByDeliveryProgress()
    {
        var c = await Setup(); var change = await Change(c, 3);
        c.Mail.DuringSend = async () => { await c.Guest.ReduceAsync(c.Id, c.Access, "salad:Olena", 1, Now); };
        await c.Notifications.SendAsync("workshop-group", change.NotificationId, "workshop-owner-sub");
        Assert.Equal(12m, (await c.Guest.GetAsync(c.Id, c.Access)).ConfirmedTotal);
        var token = c.Mail.Changes.Single(m => m.To == "guest@example.test").Confirmation!.ConfirmationToken;
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => c.Guest.ConfirmAsync(c.Id, token, Now));
    }

    [Fact]
    public async Task ClearKeepsNamedNoticesAndPersonalAccessAndAllowsChoosingAgain()
    {
        var c = await Setup(); await c.Owner.ClearAsync("workshop-group", c.Id, "workshop-owner-sub", null);
        var view = await c.Guest.GetAsync(c.Id, c.Access);
        Assert.Empty(view.Confirmed); Assert.Equal("Salad", Assert.Single(view.Notices).DishName);
        Assert.Equal("Страву скасовано", Assert.Single(view.Notices).Message);
        await Change(c, 1);
        Assert.Single((await c.Guest.GetAsync(c.Id, c.Access)).Pending!);
    }

    [Fact]
    public async Task RemovingGuestRevokesAccessAndPersistsAuditAndNotificationInTheSameWrite()
    {
        var c = await Setup(); var result = await c.Owner.RemoveAsync("workshop-group", c.Id, "workshop-owner-sub", null);
        Assert.NotNull(result.NotificationId); Assert.DoesNotContain(result.Participants, p => p.Id == c.Id);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => c.Guest.GetAsync(c.Id, c.Access));
        Assert.Equal("RemoveGuest", Assert.Single(await c.Owner.HistoryAsync("workshop-group", "workshop-owner-sub")).Action);
        await c.Notifications.SendAsync("workshop-group", result.NotificationId!, "workshop-owner-sub");
        Assert.Equal(2, c.Mail.Changes.Count); Assert.All(c.Mail.Changes, mail => Assert.Null(mail.Confirmation));
    }

    [Fact]
    public async Task ReminderIsLimitedToPendingDishesAndOnePerMinute()
    {
        var c = await Setup();
        Assert.Equal(400, (await Assert.ThrowsAsync<ApiException>(() => c.Owner.RemindAsync("workshop-group", c.Id, "workshop-owner-sub", null))).Status);
        await c.Guest.ProposeAsync(c.Id, c.Access, new("salad:Olena", "salad", "Salad", "Olena", 12m, 3), Now);
        var result = await c.Owner.RemindAsync("workshop-group", c.Id, "workshop-owner-sub", null);
        Assert.Equal(429, (await Assert.ThrowsAsync<ApiException>(() => c.Owner.RemindAsync("workshop-group", c.Id, "workshop-owner-sub", null))).Status);
        await c.Notifications.SendAsync("workshop-group", result.NotificationId, "workshop-owner-sub");
        Assert.NotNull(c.Mail.Changes.Single(m => m.To == "guest@example.test").Confirmation);
    }

    [Fact]
    public async Task ClearAllIncludesTheOrganizerAndKeepsParticipantsAndNamedGuestNotices()
    {
        var c = await Setup();
        var group = (await c.Groups.GetGroupAsync("workshop-group", default))!;
        var updated = group with
        {
            Version = group.Version + 1,
            Invite = group.Invite with { StartTime = Now.AddHours(2).ToString("O") },
            Participants = group.Participants.Select(p => p.Type == "Owner" ? p with
            { IsSubmitted = true, Items = [new(new("soup", "Soup", 8), 1)] } : p).ToArray()
        };
        Assert.True(await c.Groups.SaveAsync(updated, group.Version, null, default));
        var result = await c.Owner.ClearAllAsync(group.Id, group.OwnerSubject, null);
        Assert.Equal(0, result.Snapshot.Total); Assert.Equal(2, result.Snapshot.Participants.Length);
        Assert.All(result.Snapshot.Participants, p => Assert.Empty(p.Items)); Assert.Equal(2, result.NotificationIds.Length);
        var view = await c.Guest.GetAsync(c.Id, c.Access); Assert.Equal("Salad", Assert.Single(view.Notices).DishName);
        foreach (var id in result.NotificationIds) await c.Notifications.SendAsync(group.Id, id, group.OwnerSubject);
        Assert.Equal(3, c.Mail.Changes.Count); // Guest + organizer for guest change; one copy for organizer's own dishes.
    }

    [Fact]
    public async Task ClearAllRejectsExpiredGuestWithoutPartiallyClearingOtherGuests()
    {
        var c = await Setup();
        var second = await c.Guest.CreateDraftAsync("workshop-group", "Second", "second@example.test", Now.AddMinutes(1), Now, TimeSpan.FromMinutes(1));
        await c.Guest.ProposeAsync(second.OrderId, second.DraftAccessToken, new("soup:Second", "soup", "Soup", "Second", 8, 1), Now);
        var late = new OwnerGuestOrderService(c.Groups, new ConfigurationBuilder().Build(), new FixedClock(Now.AddMinutes(2)));
        Assert.Equal(409, (await Assert.ThrowsAsync<ApiException>(() => late.ClearAllAsync("workshop-group", "workshop-owner-sub", null))).Status);
        Assert.Equal(24m, (await c.Guest.GetAsync(c.Id, c.Access)).ConfirmedTotal);
        Assert.Empty(await c.Owner.HistoryAsync("workshop-group", "workshop-owner-sub"));
        Assert.Empty(await c.Notifications.StatusAsync("workshop-group", "workshop-owner-sub"));
    }

    [Fact]
    public async Task ForeignOwnerCannotChangeReadHistoryOrSendEmails()
    {
        var c = await Setup(); var change = await Change(c, 1);
        Assert.Equal(403, (await Assert.ThrowsAsync<ApiException>(() => Change(c, 0, "foreign-owner"))).Status);
        Assert.Equal(403, (await Assert.ThrowsAsync<ApiException>(() => c.Owner.HistoryAsync("workshop-group", "foreign-owner"))).Status);
        Assert.Equal(403, (await Assert.ThrowsAsync<ApiException>(() => c.Notifications.SendAsync("workshop-group", change.NotificationId, "foreign-owner"))).Status);
        Assert.Empty(c.Mail.Changes);
    }

    [Fact]
    public async Task UnverifiedOwnerEmailBlocksEditWithoutSideEffects()
    {
        var c = await Setup(false);
        Assert.Equal(409, (await Assert.ThrowsAsync<ApiException>(() => Change(c, 1))).Status);
        Assert.Equal(24m, (await c.Guest.GetAsync(c.Id, c.Access)).ConfirmedTotal);
        Assert.Empty(await c.Owner.HistoryAsync("workshop-group", "workshop-owner-sub"));
    }

    [Theory]
    [InlineData(-1)] [InlineData(100)]
    public async Task InvalidQuantitiesDoNotCreateAuditOrEmails(int quantity)
    {
        var c = await Setup(); Assert.Equal(400, (await Assert.ThrowsAsync<ApiException>(() => Change(c, quantity))).Status);
        Assert.Empty(await c.Owner.HistoryAsync("workshop-group", "workshop-owner-sub"));
    }

    [Fact]
    public async Task ASecondNotificationSenderCannotAcquireAnActiveLease()
    {
        var c = await Setup(); var change = await Change(c, 1);
        c.Mail.DuringSend = async () => Assert.Equal(409, (await Assert.ThrowsAsync<ApiException>(() =>
            c.Notifications.SendAsync("workshop-group", change.NotificationId, "workshop-owner-sub"))).Status);
        await c.Notifications.SendAsync("workshop-group", change.NotificationId, "workshop-owner-sub");
        Assert.Equal(2, c.Mail.Changes.Count);
    }

    [Fact]
    public async Task ARejectedWriteDoesNotLeaveAnEditWithoutItsAuditAndNotification()
    {
        var c = await Setup();
        var service = new OwnerGuestOrderService(new RejectWrites(c.Groups), new ConfigurationBuilder().Build(), new Clock());
        Assert.Equal(409, (await Assert.ThrowsAsync<ApiException>(() => service.ChangeAsync("workshop-group", c.Id,
            "workshop-owner-sub", null, new("salad", "Olena", 1)))).Status);
        Assert.Equal(24m, (await c.Guest.GetAsync(c.Id, c.Access)).ConfirmedTotal);
        var group = (await c.Groups.GetGroupAsync("workshop-group", default))!;
        Assert.Empty(group.Audit ?? []); Assert.Empty(group.Notifications ?? []);
    }

    private sealed class RejectWrites(IGroupRepository inner) : IGroupRepository
    {
        public Task<GroupAggregate?> GetGroupAsync(string id, CancellationToken ct) => inner.GetGroupAsync(id, ct);
        public Task<string?> FindGroupByReservationAsync(string id, CancellationToken ct) => inner.FindGroupByReservationAsync(id, ct);
        public Task<string?> FindGroupBySessionAsync(string id, CancellationToken ct) => inner.FindGroupBySessionAsync(id, ct);
        public Task<string?> FindGroupByInviteAsync(string id, CancellationToken ct) => inner.FindGroupByInviteAsync(id, ct);
        public Task<bool> SaveAsync(GroupAggregate group, long version, string? removed, CancellationToken ct) => Task.FromResult(false);
    }

    [Fact]
    public async Task OwnerCannotEditAtDeadlineOrAfterSubmission()
    {
        var c = await Setup();
        var group = (await c.Groups.GetGroupAsync("workshop-group", default))!;
        var config = new ConfigurationBuilder().Build();
        var late = new OwnerGuestOrderService(c.Groups, config, new FixedClock(Now.AddMinutes(30)));
        Assert.Equal(409, (await Assert.ThrowsAsync<ApiException>(() => late.ClearAsync(group.Id, c.Id, group.OwnerSubject, null))).Status);
        await new GroupService(c.Groups, new Clock()).SubmitAsync(group.Id, group.ReservationId, group.OwnerSubject);
        Assert.Equal(409, (await Assert.ThrowsAsync<ApiException>(() => Change(c, 1))).Status);
    }
    private sealed class FixedClock(DateTimeOffset time) : TimeProvider { public override DateTimeOffset GetUtcNow() => time; }
}
