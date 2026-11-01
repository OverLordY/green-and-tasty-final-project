using Microsoft.Extensions.Configuration;
using Restaurant.Api;
using Xunit;

namespace Restaurant.Api.Tests;

public sealed class RestaurantOrderTests
{
    private sealed class Clock : TimeProvider { public override DateTimeOffset GetUtcNow() => new(2030, 1, 1, 17, 0, 0, TimeSpan.Zero); }
    private sealed class Mail : IGuestConfirmationMailer
    {
        public GuestConfirmationMail? Message;
        public Task SendAsync(GuestConfirmationMail message, CancellationToken ct) { Message = message; return Task.CompletedTask; }
    }
    [Fact]
    public async Task ConfirmedGuestToOwnerToRestaurantJourneyExcludesUnconfirmedExtraPortions()
    {
        var group = DevelopmentFixture.Create() with { Participants = [new("workshop-owner-session", "Owner", "Owner", false, [])] };
        var groups = new InMemoryGroupRepository([group]); var mail = new Mail(); var clock = new Clock();
        var guest = new GuestOrderService(new GroupGuestOrderStore(groups, Settings()), mail, groups, clock);
        var now = clock.GetUtcNow();
        var draft = await guest.CreateDraftAsync(group.Id, "Andrii", "guest@example.test", now.AddMinutes(30), now, TimeSpan.FromMinutes(15));
        var line = new GuestOrderLine("salad:Olena", "salad", "Salad", "Olena", 12, 2);
        await guest.ProposeAsync(draft.OrderId, draft.DraftAccessToken, line, now);
        await guest.RequestConfirmationAsync(draft.OrderId, draft.DraftAccessToken, now);
        var access = (await guest.ConfirmAsync(draft.OrderId, mail.Message!.ConfirmationToken, now)).GuestAccessToken!;
        await guest.ProposeAsync(draft.OrderId, access, line with { Quantity = 3 }, now);
        await new GroupService(groups, clock).SubmitAsync(group.Id, group.ReservationId, group.OwnerSubject);
        var receipt = await new RestaurantOrderService(groups, Settings()).GetAsync(group.ReservationId, "waiter");
        Assert.Equal(2, Assert.Single(receipt.Items).Quantity); Assert.Equal(24m, receipt.Total);
        Assert.False((await guest.GetAsync(draft.OrderId, access)).CanChange);
        await Assert.ThrowsAsync<InvalidOperationException>(() => guest.ReduceAsync(draft.OrderId, access, line.Key, 1, now));
    }
    private static IConfiguration Settings() => new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
    { ["Restaurant:Staff:0:Subject"] = "waiter", ["Restaurant:Staff:0:LocationId"] = "workshop-location",
      ["Restaurant:Staff:1:Subject"] = "other-waiter", ["Restaurant:Staff:1:LocationId"] = "other-location" }).Build();
    [Fact]
    public async Task ReceiptContainsOnlySubmittedDishesAndFixedAmountsWithoutGuestCredentials()
    {
        var group = DevelopmentFixture.Create(); var groups = new InMemoryGroupRepository([group]);
        var owner = new GroupService(groups);
        await owner.SubmitAsync(group.Id, group.ReservationId, group.OwnerSubject);
        var receipt = await new RestaurantOrderService(groups, Settings()).GetAsync(group.ReservationId, "waiter");
        Assert.Equal(3, receipt.Items.Single(i => i.DishId == "dish-salad").Quantity);
        Assert.Equal(1, receipt.Items.Single(i => i.DishId == "dish-bowl").Quantity);
        Assert.Equal(50.5m, receipt.Total);
        var saved = (await groups.GetGroupAsync(group.Id, default))!;
        Assert.True(await groups.SaveAsync(saved with { Participants = [], Version = saved.Version + 1 }, saved.Version, null, default));
        Assert.Equal(50.5m, (await new RestaurantOrderService(groups, Settings()).GetAsync(group.ReservationId, "waiter")).Total);
    }
    [Theory]
    [InlineData("workshop-owner-sub")] [InlineData("guest")] [InlineData("other-waiter")] [InlineData("")]
    public async Task OwnerGuestAndUnassignedStaffCannotReadRestaurantOrder(string subject)
    {
        var groups = new InMemoryGroupRepository([DevelopmentFixture.Create()]);
        var service = new RestaurantOrderService(groups, Settings());
        Assert.Equal(403, (await Assert.ThrowsAsync<ApiException>(() => service.GetAsync("workshop-reservation", subject))).Status);
    }
    [Fact]
    public async Task DraftAndMissingReservationsHaveNoRestaurantReceipt()
    {
        var service = new RestaurantOrderService(new InMemoryGroupRepository([DevelopmentFixture.Create()]), Settings());
        Assert.Equal(404, (await Assert.ThrowsAsync<ApiException>(() => service.GetAsync("workshop-reservation", "waiter"))).Status);
        Assert.Equal(404, (await Assert.ThrowsAsync<ApiException>(() => service.GetAsync("unknown", "waiter"))).Status);
    }
}
