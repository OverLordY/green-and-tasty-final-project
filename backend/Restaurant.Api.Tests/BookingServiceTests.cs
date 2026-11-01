using Microsoft.Extensions.Configuration;
using Restaurant.Api;
using Xunit;

namespace Restaurant.Api.Tests;

public sealed class BookingServiceTests
{
    private sealed class Clock : TimeProvider { public override DateTimeOffset GetUtcNow() => new(2030, 1, 1, 12, 0, 0, TimeSpan.Zero); }
    private static IConfiguration Config() => new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["Restaurant:Tables:0:Id"] = "table", ["Restaurant:Tables:0:LocationId"] = "location", ["Restaurant:Tables:0:Number"] = "7", ["Restaurant:Tables:0:Capacity"] = "4",
        ["Restaurant:Locations:0:Id"] = "location", ["Restaurant:Locations:0:RestaurantName"] = "Restaurant", ["Restaurant:Locations:0:Address"] = "Address"
    }).Build();
    [Fact]
    public async Task StaffSeeOnlyAssignedLocationAndNoOwnerIdentity()
    {
        var config = new ConfigurationBuilder().AddConfiguration(Config()).AddInMemoryCollection(new Dictionary<string, string?>
        { ["Restaurant:Staff:0:Subject"] = "waiter", ["Restaurant:Staff:0:LocationId"] = "location",
          ["Restaurant:Staff:1:Subject"] = "foreign", ["Restaurant:Staff:1:LocationId"] = "other" }).Build();
        var store = new InMemoryGroupRepository([]); var clock = new Clock(); var now = clock.GetUtcNow();
        var service = new BookingService(store, config, clock);
        var created = await service.CreateAsync(new("table", now.AddHours(2), now.AddHours(3), 2), "private-subject", "private@example.test", "Owner");
        var result = await service.ListForStaffAsync(store, "waiter");
        Assert.Equal(created.Id, Assert.Single(result).Id);
        Assert.Null(result[0].SubmittedDishCount);
        var groupId = (await store.FindGroupByReservationAsync(created.Id, default))!;
        var group = (await store.GetGroupAsync(groupId, default))!;
        var receipt = new RestaurantOrderReceipt("order", created.Id, "location", 7, group.Invite.StartTime, now,
            [new("salad", "Salad", 2, 24m), new("soup", "Soup", 1, 8m)]);
        Assert.True(await store.SaveAsync(group with { Status = "Submitted", RestaurantReceipt = receipt, Version = group.Version + 1 }, group.Version, null, default));
        var submitted = Assert.Single(await service.ListForStaffAsync(store, "waiter"));
        Assert.True(submitted.HasSubmittedPreOrder); Assert.Equal(3, submitted.SubmittedDishCount);
        var json = System.Text.Json.JsonSerializer.Serialize(result);
        Assert.DoesNotContain("private@example.test", json); Assert.DoesNotContain("private-subject", json);
        Assert.Empty(await service.ListForStaffAsync(store, "foreign"));
        Assert.Equal(403, (await Assert.ThrowsAsync<ApiException>(() => service.ListForStaffAsync(store, "owner"))).Status);
    }

    [Fact]
    public async Task CancellationReleasesTableClosesGroupAndQueuesGuestNotices()
    {
        var store = new InMemoryGroupRepository([]); var clock = new Clock(); var now = clock.GetUtcNow();
        var service = new BookingService(store, Config(), clock); var request = new CreateBookingRequest("table", now.AddHours(2), now.AddHours(3), 2);
        var booking = await service.CreateAsync(request, "owner", "owner@example.test", "Owner");
        var groupId = (await store.FindGroupByReservationAsync(booking.Id, default))!;
        var guest = new GuestOrderService(new GroupGuestOrderStore(store, Config()), new FakeMail(), store, clock);
        var draft = await guest.CreateDraftAsync(groupId, "Guest", "guest@example.test", now.AddMinutes(75), now, TimeSpan.FromMinutes(15));
        await Assert.ThrowsAsync<ApiException>(() => service.CancelAsync(booking.Id, "other", store, store));
        var cancelled = await service.CancelAsync(booking.Id, "owner", store, store);
        Assert.Single(cancelled.NotificationIds);
        var group = (await store.GetGroupAsync(groupId, default))!;
        Assert.Equal("Closed", group.Status); Assert.True(group.ReservationCancelled); Assert.Single(group.Audit!); Assert.Single(group.Notifications!);
        var view = await guest.GetAsync(draft.OrderId, draft.DraftAccessToken);
        Assert.True(view.ReservationCancelled); Assert.False(view.CanChange);
        await Assert.ThrowsAsync<InvalidOperationException>(() => guest.ProposeAsync(draft.OrderId, draft.DraftAccessToken, new("dish:Guest", "dish", "Dish", "Guest", 1, 1), now));
        Assert.Empty((await store.ReadScheduleAsync("table")).Intervals);
        Assert.Equal(1, Assert.Single(await service.ListAsync(store, "owner")).Status);
        Assert.Empty((await service.CancelAsync(booking.Id, "owner", store, store)).NotificationIds);
        var replacement = await service.CreateAsync(request, "owner2", "other@example.test", "Other");
        Assert.NotEqual(booking.Id, replacement.Id);
    }

    [Fact]
    public async Task CancellationRefusesSubmittedOrders()
    {
        var store = new InMemoryGroupRepository([]); var clock = new Clock(); var now = clock.GetUtcNow();
        var service = new BookingService(store, Config(), clock);
        var booking = await service.CreateAsync(new("table", now.AddHours(2), now.AddHours(3), 2), "owner", "owner@example.test", "Owner");
        var id = (await store.FindGroupByReservationAsync(booking.Id, default))!; var group = (await store.GetGroupAsync(id, default))!;
        Assert.True(await store.SaveAsync(group with { Status = "Submitted", Version = group.Version + 1 }, group.Version, null, default));
        Assert.Equal(409, (await Assert.ThrowsAsync<ApiException>(() => service.CancelAsync(booking.Id, "owner", store, store))).Status);
        Assert.Single((await store.ReadScheduleAsync("table")).Intervals);
    }
    private sealed class FakeMail : IGuestConfirmationMailer { public Task SendAsync(GuestConfirmationMail mail, CancellationToken ct) => Task.CompletedTask; }

    private sealed class CaptureMail : IGuestConfirmationMailer
    {
        public GuestConfirmationMail? Message { get; private set; }
        public Task SendAsync(GuestConfirmationMail mail, CancellationToken ct) { Message = mail; return Task.CompletedTask; }
    }
    private sealed class CaptureChangeMail : IOrderChangeMailer
    {
        public List<OrderChangeMail> Messages { get; } = [];
        public Task SendAsync(OrderChangeMail mail, CancellationToken ct) { Messages.Add(mail); return Task.CompletedTask; }
    }

    [Fact]
    public async Task EditingMovesScheduleAndDeadlinesPreservesConfirmedDishesAndReissuesPendingConfirmation()
    {
        var store = new InMemoryGroupRepository([]); var clock = new Clock(); var now = clock.GetUtcNow();
        var service = new BookingService(store, Config(), clock);
        var booking = await service.CreateAsync(new("table", now.AddHours(2), now.AddHours(3), 3), "owner", "owner@example.test", "Owner");
        var groupId = (await store.FindGroupByReservationAsync(booking.Id, default))!;
        var mail = new CaptureMail(); var guest = new GuestOrderService(new GroupGuestOrderStore(store, Config()), mail, store, clock);
        var draft = await guest.CreateDraftAsync(groupId, "Guest", "guest@example.test", now.AddMinutes(75), now, TimeSpan.FromMinutes(15));
        var line = new GuestOrderLine("dish:Guest", "dish", "Dish", "Guest", 5m, 1);
        await guest.ProposeAsync(draft.OrderId, draft.DraftAccessToken, line, now);
        await guest.RequestConfirmationAsync(draft.OrderId, draft.DraftAccessToken, now);
        var access = (await guest.ConfirmAsync(draft.OrderId, mail.Message!.ConfirmationToken, now)).GuestAccessToken!;
        await guest.ProposeAsync(draft.OrderId, access, line with { Quantity = 2 }, now.AddMinutes(1));
        await guest.RequestConfirmationAsync(draft.OrderId, access, now.AddMinutes(1));
        var obsolete = mail.Message!.ConfirmationToken;
        var edited = await service.EditAsync(new(booking.Id, now.AddHours(3), now.AddHours(4), 4), "owner", store, store);
        Assert.Single(edited.NotificationIds); Assert.Equal(4, edited.Reservation.GuestCount);
        var group = (await store.GetGroupAsync(groupId, default))!;
        Assert.Equal(now.AddMinutes(150), group.SubmissionDeadline);
        Assert.Equal(now.AddMinutes(135), group.GuestOrders![draft.OrderId].State.ChangeDeadline);
        Assert.True(Assert.Single(group.Notifications!).NeedsGuestConfirmation);
        Assert.Equal(1, Assert.Single((await guest.GetAsync(draft.OrderId, access)).Confirmed).Quantity);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => guest.ConfirmAsync(draft.OrderId, obsolete, now.AddMinutes(1)));
        var changeMail = new CaptureChangeMail();
        var notificationService = new OwnerNotificationService(store, changeMail, clock);
        var delivery = await notificationService.SendAsync(groupId, edited.NotificationIds[0], "owner");
        Assert.True(delivery.GuestAccepted); Assert.True(delivery.OwnerAccepted);
        var guestNotice = Assert.Single(changeMail.Messages, m => m.To == "guest@example.test");
        var ownerNotice = Assert.Single(changeMail.Messages, m => m.To == "owner@example.test");
        Assert.NotNull(guestNotice.Confirmation); Assert.Null(ownerNotice.Confirmation);
        Assert.Equal(2, Assert.Single(guestNotice.Confirmation!.Items).Quantity);
        var receipt = await guest.ConfirmAsync(draft.OrderId, guestNotice.Confirmation.ConfirmationToken, now.AddMinutes(1));
        Assert.Equal(2, Assert.Single((await guest.GetAsync(draft.OrderId, receipt.GuestAccessToken!)).Confirmed).Quantity);
        var interval = Assert.Single((await store.ReadScheduleAsync("table")).Intervals);
        Assert.Equal(now.AddHours(3), interval.Start); Assert.Equal(now.AddHours(4), interval.End);
    }

    [Fact]
    public async Task ChangingGuestCountKeepsExistingConfirmationLinkAndNotifiesWithoutClaimingDishCancellation()
    {
        var store = new InMemoryGroupRepository([]); var clock = new Clock(); var now = clock.GetUtcNow();
        var service = new BookingService(store, Config(), clock);
        var booking = await service.CreateAsync(new("table", now.AddHours(2), now.AddHours(3), 2), "owner", "owner@example.test", "Owner");
        var id = (await store.FindGroupByReservationAsync(booking.Id, default))!;
        var mail = new CaptureMail(); var guest = new GuestOrderService(new GroupGuestOrderStore(store, Config()), mail, store, clock);
        var draft = await guest.CreateDraftAsync(id, "Guest", "guest@example.test", now.AddMinutes(75), now, TimeSpan.FromMinutes(15));
        await guest.ProposeAsync(draft.OrderId, draft.DraftAccessToken, new("dish:Guest", "dish", "Dish", "Guest", 5m, 1), now);
        await guest.RequestConfirmationAsync(draft.OrderId, draft.DraftAccessToken, now);
        var existingLink = mail.Message!.ConfirmationToken;
        await service.EditAsync(new(booking.Id, now.AddHours(2), now.AddHours(3), 3), "owner", store, store);
        var group = (await store.GetGroupAsync(id, default))!;
        var notice = Assert.Single(group.Notifications!);
        Assert.False(notice.NeedsGuestConfirmation); Assert.True(notice.IsReservationChange);
        Assert.Empty(notice.Items);
        var text = OrderChangeMailText.Format(new("guest@example.test", notice.Summary, notice.Items, false, IsReservationChange: true), Config());
        Assert.DoesNotContain("Зменшення та скасування", text);
        Assert.Contains("Непідтверджені зміни", text);
        Assert.NotNull((await guest.ConfirmAsync(draft.OrderId, existingLink, now)).GuestAccessToken);
    }

    [Fact]
    public async Task EditingRejectsUnauthorizedOverlappingTooFewAndSubmittedWithoutPartialWrites()
    {
        var store = new InMemoryGroupRepository([]); var clock = new Clock(); var now = clock.GetUtcNow();
        var service = new BookingService(store, Config(), clock);
        var first = await service.CreateAsync(new("table", now.AddHours(2), now.AddHours(3), 3), "owner", "owner@example.test", "Owner");
        await service.CreateAsync(new("table", now.AddHours(4), now.AddHours(5), 2), "other", "other@example.test", "Other");
        var id = (await store.FindGroupByReservationAsync(first.Id, default))!;
        var guest = new GuestOrderService(new GroupGuestOrderStore(store, Config()), new FakeMail(), store, clock);
        await guest.CreateDraftAsync(id, "Guest", "guest@example.test", now.AddMinutes(75), now, TimeSpan.FromMinutes(15));
        var valid = new EditBookingRequest(first.Id, now.AddHours(3), now.AddHours(4), 3);
        Assert.Equal(403, (await Assert.ThrowsAsync<ApiException>(() => service.EditAsync(valid, "other", store, store))).Status);
        Assert.Equal(409, (await Assert.ThrowsAsync<ApiException>(() => service.EditAsync(valid with { GuestCount = 1 }, "owner", store, store))).Status);
        Assert.Equal(409, (await Assert.ThrowsAsync<ApiException>(() => service.EditAsync(valid with { StartTime = now.AddHours(4), EndTime = now.AddHours(5) }, "owner", store, store))).Status);
        Assert.Equal(400, (await Assert.ThrowsAsync<ApiException>(() => service.EditAsync(valid with { StartTime = now.AddMinutes(30) }, "owner", store, store))).Status);
        var before = (await store.GetGroupAsync(id, default))!;
        Assert.Equal(now.AddHours(2), DateTimeOffset.Parse(before.Invite.StartTime));
        Assert.True(await store.SaveAsync(before with { Status = "Submitted", Version = before.Version + 1 }, before.Version, null, default));
        Assert.Equal(409, (await Assert.ThrowsAsync<ApiException>(() => service.EditAsync(valid, "owner", store, store))).Status);
        Assert.Equal(2, (await store.ReadScheduleAsync("table")).Intervals.Length);
    }

    [Fact]
    public async Task OwnerListContainsOnlyTheirOwnBookings()
    {
        var store = new InMemoryGroupRepository([]); var clock = new Clock(); var now = clock.GetUtcNow();
        var service = new BookingService(store, Config(), clock);
        var first = await service.CreateAsync(new("table", now.AddHours(2), now.AddHours(3), 2), "owner-a", "a@example.test", "A");
        await service.CreateAsync(new("table", now.AddHours(3), now.AddHours(4), 2), "owner-b", "b@example.test", "B");
        var bookings = await service.ListAsync(store, "owner-a");
        Assert.Equal(first.Id, Assert.Single(bookings).Id);
        Assert.DoesNotContain("b@example.test", System.Text.Json.JsonSerializer.Serialize(bookings));
        Assert.Empty(await service.ListAsync(store, "other-owner"));
    }

    [Fact]
    public async Task CreatesOwnerGroupAndInviteWithServerOwnedTableDetails()
    {
        var store = new InMemoryGroupRepository([]); var clock = new Clock(); var now = clock.GetUtcNow();
        var created = await new BookingService(store, Config(), clock).CreateAsync(new("table", now.AddHours(2), now.AddHours(3), 3), "owner", "owner@example.test", "Andrii");
        var id = await store.FindGroupByReservationAsync(created.Id, default); var group = (await store.GetGroupAsync(id!, default))!;
        Assert.Equal("owner", group.OwnerSubject); Assert.Equal("owner@example.test", group.OwnerEmail);
        Assert.Equal(7, group.Invite.TableNumber); Assert.Equal("Address", group.Invite.LocationAddress);
        Assert.Equal(group.Id, await store.FindGroupByInviteAsync(group.InviteToken, default));
        Assert.Equal(now.AddMinutes(90), group.SubmissionDeadline);
    }
    [Fact]
    public async Task TwoConcurrentOverlappingBookingsHaveOnlyOneWinner()
    {
        var store = new InMemoryGroupRepository([]); var clock = new Clock(); var now = clock.GetUtcNow();
        var service = new BookingService(store, Config(), clock); var request = new CreateBookingRequest("table", now.AddHours(2), now.AddHours(3), 2);
        async Task<bool> Create() { try { await service.CreateAsync(request, "owner", "owner@example.test", "Owner"); return true; } catch (ApiException ex) when (ex.Status == 409) { return false; } }
        var results = await Task.WhenAll(Task.Run(Create), Task.Run(Create));
        Assert.Single(results, value => value); Assert.Single(results, value => !value);
    }
    [Fact]
    public async Task UnverifiedOrganizerCannotReserve()
    {
        var service = new BookingService(new InMemoryGroupRepository([]), Config(), new Clock());
        Assert.Equal(403, (await Assert.ThrowsAsync<ApiException>(() => service.CreateAsync(new("table", DateTimeOffset.MaxValue.AddHours(-2), DateTimeOffset.MaxValue, 2), "owner", null, null))).Status);
    }
}
