using Microsoft.Extensions.Configuration;
using Restaurant.Api;
using Xunit;

namespace Restaurant.Api.Tests;

public sealed class ExampleCatalogJourneyTests
{
    private sealed class Clock : TimeProvider { public override DateTimeOffset GetUtcNow() => new(2030, 1, 1, 12, 0, 0, TimeSpan.Zero); }
    private sealed class Mail : IGuestConfirmationMailer
    {
        public GuestConfirmationMail? Message;
        public Task SendAsync(GuestConfirmationMail message, CancellationToken ct) { Message = message; return Task.CompletedTask; }
    }
    [Fact]
    public async Task ExampleCatalogSupportsBookingGuestConfirmationAndRestaurantReceipt()
    {
        var path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../Restaurant.Api/appsettings.Catalog.example.json"));
        var config = new ConfigurationBuilder().AddJsonFile(path).Build();
        var locations = config.GetSection("Restaurant:Locations").Get<BookingLocation[]>()!;
        var tables = config.GetSection("Restaurant:Tables").Get<BookingTable[]>()!;
        Assert.Equal(3, locations.Length); Assert.Equal(6, tables.Length);
        var selected = tables[0]; var menu = RestaurantMenu.ForLocation(config, selected.LocationId);
        Assert.NotEmpty(menu); Assert.All(menu, dish => Assert.Equal(selected.LocationId, dish.LocationId));
        var store = new InMemoryGroupRepository([]); var clock = new Clock(); var now = clock.GetUtcNow();
        var discovery = new BookingDiscoveryService(store, config, clock);
        var publicMenu = discovery.Dishes(locationId: selected.LocationId, sortBy: "PriceAscending");
        Assert.Equal(menu.Length, publicMenu.Length);
        Assert.Equal(publicMenu.Select(item => item.Price).Order(), publicMenu.Select(item => item.Price));
        Assert.DoesNotContain(discovery.Dishes(locationId: tables[2].LocationId), item => publicMenu.Any(selectedDish => selectedDish.Id == item.Id));
        Assert.Contains(discovery.Dishes(name: dishName(menu[0].Name)), item => item.Id == menu[0].Id);
        Assert.Contains(await discovery.TablesAsync(selected.LocationId, 2, now.AddHours(2)), item => item.Id == selected.Id);
        var booking = await new BookingService(store, config, clock).CreateAsync(new(selected.Id, now.AddHours(2), now.AddHours(3.5), 2),
            "owner", "owner@example.test", "Owner");
        var groupId = (await store.FindGroupByReservationAsync(booking.Id, default))!;
        var group = (await store.GetGroupAsync(groupId, default))!;
        var mail = new Mail(); var guest = new GuestOrderService(new GroupGuestOrderStore(store, config), mail, store, clock);
        var draft = await guest.CreateDraftAsync(groupId, "Guest", "guest@example.test", now.AddMinutes(75), now, TimeSpan.FromMinutes(15));
        var dish = menu[0]; await guest.ProposeAsync(draft.OrderId, draft.DraftAccessToken,
            new(dish.Id + ":Guest", dish.Id, dish.Name, "Guest", dish.Price, 1), now);
        await guest.RequestConfirmationAsync(draft.OrderId, draft.DraftAccessToken, now);
        await guest.ConfirmAsync(draft.OrderId, mail.Message!.ConfirmationToken, now);
        var submitted = await new GroupService(store, clock).SubmitAsync(groupId, booking.Id, "owner");
        Assert.Equal(dish.Id, Assert.Single(submitted).DishId);
        var saved = (await store.GetGroupAsync(groupId, default))!;
        Assert.Equal(dish.Price, saved.RestaurantReceipt!.Total); Assert.Equal(group.Invite.LocationId, saved.RestaurantReceipt.LocationId);
    }
    private static string dishName(string value) => value[..Math.Min(4, value.Length)].ToUpperInvariant();
}
