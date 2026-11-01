using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using Xunit;

namespace Restaurant.Api.Tests;

public sealed class HttpContractTests
{
    private sealed class CapturingGuestMailer : IGuestConfirmationMailer
    {
        public List<GuestConfirmationMail> Messages { get; } = [];
        public Task SendAsync(GuestConfirmationMail message, CancellationToken cancellationToken)
        {
            Messages.Add(message);
            return Task.CompletedTask;
        }
    }

    private sealed class ApiFactory(bool useCognitoIssuer = false, bool includeForeignGroup = false) : WebApplicationFactory<Program>
    {
        public CapturingGuestMailer GuestMailer { get; } = new();
        private readonly RSA rsa = RSA.Create(2048);
        private string Issuer => useCognitoIssuer
            ? "https://cognito-idp.eu-north-1.amazonaws.com/eu-north-1_Workshop"
            : "https://workshop-issuer.test";
        private const string Audience = "workshop-api";
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development")
                .UseSetting("Authentication:Authority", Issuer)
                .UseSetting("Authentication:Audience", Audience)
                .UseSetting("Storage:UseInMemory", "true");
            builder.UseSetting("Restaurant:Staff:0:Subject", "restaurant-waiter")
                .UseSetting("Restaurant:Staff:0:LocationId", "workshop-location")
                .UseSetting("Restaurant:Staff:1:Subject", "foreign-waiter")
                .UseSetting("Restaurant:Staff:1:LocationId", "other-location");
            builder.UseSetting("Restaurant:Tables:0:Id", "table-7").UseSetting("Restaurant:Tables:0:LocationId", "workshop-location")
                .UseSetting("Restaurant:Tables:0:Number", "7").UseSetting("Restaurant:Tables:0:Capacity", "4")
                .UseSetting("Restaurant:Locations:0:Id", "workshop-location").UseSetting("Restaurant:Locations:0:RestaurantName", "Green & Tasty")
                .UseSetting("Restaurant:Locations:0:Address", "Rustaveli Avenue 48");
            builder.UseSetting("Restaurant:Menu:0:Id", "salad").UseSetting("Restaurant:Menu:0:Name", "Salad")
                .UseSetting("Restaurant:Menu:0:Price", "10").UseSetting("Restaurant:Menu:0:Available", "true");
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IGuestConfirmationMailer>();
                services.AddSingleton<IGuestConfirmationMailer>(GuestMailer);
                services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, options =>
                {
                    var configuration = new OpenIdConnectConfiguration { Issuer = Issuer };
                    configuration.SigningKeys.Add(new RsaSecurityKey(rsa) { KeyId = "test-key" });
                    options.ConfigurationManager = new StaticConfigurationManager<OpenIdConnectConfiguration>(configuration);
                });
                if (includeForeignGroup)
                {
                    var fixture = DevelopmentFixture.Create();
                    var foreign = fixture with
                    {
                        Id = "foreign-group", ReservationId = "foreign-reservation",
                        OwnerSubject = "foreign-owner",
                        Participants = [fixture.Participants[1] with { Id = "foreign-session" }]
                    };
                    services.RemoveAll<IGroupRepository>();
                    services.AddSingleton<IGroupRepository>(new InMemoryGroupRepository([fixture, foreign]));
                }
            });
        }
        public string SignedToken(string subject, string? tokenUse = null, bool verifiedEmail = false)
        {
            var claims = new List<Claim> { new("sub", subject) };
            claims.Add(new("email", "owner@example.test"));
            claims.Add(new("email_verified", verifiedEmail ? "true" : "false"));
            if (tokenUse is not null) claims.Add(new("token_use", tokenUse));
            var token = new JwtSecurityToken(Issuer, Audience, claims,
                notBefore: DateTime.UtcNow.AddMinutes(-1), expires: DateTime.UtcNow.AddMinutes(5),
                signingCredentials: new SigningCredentials(new RsaSecurityKey(rsa) { KeyId = "test-key" }, SecurityAlgorithms.RsaSha256));
            return new JwtSecurityTokenHandler().WriteToken(token);
        }
        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing) rsa.Dispose();
        }
    }

    [Fact]
    public async Task ReservationEditRequiresOwnerAndReturnsUpdatedBooking()
    {
        using var factory = new ApiFactory(); using var client = factory.CreateClient(); var now = DateTimeOffset.UtcNow;
        client.DefaultRequestHeaders.Authorization = new("Bearer", factory.SignedToken("owner", verifiedEmail: true));
        var response = await client.PostAsJsonAsync("/reservations", new CreateBookingRequest("table-7", now.AddHours(2), now.AddHours(3), 2));
        var booking = (await response.Content.ReadFromJsonAsync<CreatedBooking>())!;
        var request = new EditBookingRequest(booking.Id, now.AddHours(3), now.AddHours(4), 3);
        client.DefaultRequestHeaders.Authorization = null;
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/reservations/edit", request)).StatusCode);
        client.DefaultRequestHeaders.Authorization = new("Bearer", factory.SignedToken("stranger", verifiedEmail: true));
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/reservations/edit", request)).StatusCode);
        client.DefaultRequestHeaders.Authorization = new("Bearer", factory.SignedToken("owner", verifiedEmail: true));
        var edited = await client.PostAsJsonAsync("/reservations/edit", request);
        Assert.Equal(HttpStatusCode.OK, edited.StatusCode);
        var result = (await edited.Content.ReadFromJsonAsync<EditBookingResult>())!;
        Assert.Equal(3, result.Reservation.GuestCount); Assert.Equal(now.AddHours(3), result.Reservation.StartTime);
        Assert.Equal(now.AddHours(3), (await client.GetFromJsonAsync<CreatedBooking[]>("/reservations"))!.Single(item => item.Id == booking.Id).StartTime);
    }

    [Fact]
    public async Task OwnerCanManageOwnDishAndSubmitItButAnotherUserCannot()
    {
        using var factory = new ApiFactory(); using var client = factory.CreateClient(); var now = DateTimeOffset.UtcNow;
        client.DefaultRequestHeaders.Authorization = new("Bearer", factory.SignedToken("owner", verifiedEmail: true));
        var response = await client.PostAsJsonAsync("/reservations", new CreateBookingRequest("table-7", now.AddHours(2), now.AddHours(3), 2));
        var booking = (await response.Content.ReadFromJsonAsync<CreatedBooking>())!;
        var groups = factory.Services.GetRequiredService<IGroupRepository>();
        var groupId = (await groups.FindGroupByReservationAsync(booking.Id, default))!;
        var path = $"/owner/groups/{groupId}/own-order/dishes";
        client.DefaultRequestHeaders.Authorization = new("Bearer", factory.SignedToken("stranger", verifiedEmail: true));
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync(path, new OwnerLineChange("salad", "Owner", 2))).StatusCode);
        client.DefaultRequestHeaders.Authorization = new("Bearer", factory.SignedToken("owner", verifiedEmail: true));
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(path, new OwnerLineChange("salad", "person@example.test", 2))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsJsonAsync(path, new OwnerLineChange("missing", "Owner", 1))).StatusCode);
        var added = await client.PostAsJsonAsync(path, new OwnerLineChange("salad", "Owner", 2));
        Assert.Equal(HttpStatusCode.OK, added.StatusCode);
        var owner = Assert.Single((await added.Content.ReadFromJsonAsync<GroupSnapshot>())!.Participants);
        Assert.True(owner.IsSubmitted); Assert.Equal(20m, owner.Total);
        var reduced = await client.PostAsJsonAsync(path, new OwnerLineChange("salad", "Owner", 1));
        var reviewed = (await reduced.Content.ReadFromJsonAsync<GroupSnapshot>())!;
        Assert.Equal(10m, reviewed.Total);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(path, new OwnerLineChange("salad", "Owner", 100))).StatusCode);
        var cleared = await client.PostAsJsonAsync(path, new OwnerLineChange("salad", "Owner", 0));
        Assert.False(Assert.Single((await cleared.Content.ReadFromJsonAsync<GroupSnapshot>())!.Participants).IsSubmitted);
        var readded = await client.PostAsJsonAsync(path, new OwnerLineChange("salad", "Owner", 1));
        Assert.Equal(HttpStatusCode.OK, readded.StatusCode);
        var latest = (await readded.Content.ReadFromJsonAsync<GroupSnapshot>())!;
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync($"/group-reservations/{groupId}/submit", new SubmitRequest(booking.Id))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync($"/group-reservations/{groupId}/submit", new SubmitRequest(booking.Id, -1))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync($"/group-reservations/{groupId}/submit", new SubmitRequest(booking.Id, reviewed.Version))).StatusCode);
        Assert.Equal("Open", (await client.GetFromJsonAsync<GroupStatus>($"/guest/groups/{groupId}/status"))!.Status);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync($"/group-reservations/{groupId}/submit", new SubmitRequest(booking.Id, latest.Version))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync(path, new OwnerLineChange("salad", "Owner", 2))).StatusCode);
        client.DefaultRequestHeaders.Authorization = new("Bearer", factory.SignedToken("restaurant-waiter"));
        var receipt = (await client.GetFromJsonAsync<RestaurantOrderReceipt>($"/restaurant/reservations/{booking.Id}/pre-order"))!;
        Assert.Equal(10m, receipt.Total); Assert.Equal(1, Assert.Single(receipt.Items).Quantity);
    }

    [Fact]
    public async Task OwnerCannotChangeOwnDishesAfterSystemDeadline()
    {
        using var factory = new ApiFactory(); using var client = factory.CreateClient(); var now = DateTimeOffset.UtcNow;
        client.DefaultRequestHeaders.Authorization = new("Bearer", factory.SignedToken("owner", verifiedEmail: true));
        var response = await client.PostAsJsonAsync("/reservations", new CreateBookingRequest("table-7", now.AddMinutes(30), now.AddHours(2), 2));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var booking = (await response.Content.ReadFromJsonAsync<CreatedBooking>())!;
        var groupId = (await factory.Services.GetRequiredService<IGroupRepository>().FindGroupByReservationAsync(booking.Id, default))!;
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync($"/owner/groups/{groupId}/own-order/dishes", new OwnerLineChange("salad", "Owner", 1))).StatusCode);
    }

    [Fact]
    public async Task ConfirmedGuestCredentialsAreIsolatedAcrossHttpEndpoints()
    {
        using var factory = new ApiFactory(); using var client = factory.CreateClient();
        var service = factory.Services.GetRequiredService<GuestOrderService>();
        var now = DateTimeOffset.UtcNow;
        var a = await service.CreateDraftAsync("workshop-group", "Guest A", "a@example.test", now.AddHours(1), now, TimeSpan.FromMinutes(15));
        var b = await service.CreateDraftAsync("workshop-group", "Guest B", "b@example.test", now.AddHours(1), now, TimeSpan.FromMinutes(15));
        var line = new GuestOrderLine("salad:A", "salad", "Salad", "A", 10m, 2);
        foreach (var draft in new[] { a, b })
        {
            await service.ProposeAsync(draft.OrderId, draft.DraftAccessToken, line, now);
            await service.RequestConfirmationAsync(draft.OrderId, draft.DraftAccessToken, now);
        }
        var tokenA = (await service.ConfirmAsync(a.OrderId, factory.GuestMailer.Messages[0].ConfirmationToken, now)).GuestAccessToken!;
        var tokenB = (await service.ConfirmAsync(b.OrderId, factory.GuestMailer.Messages[1].ConfirmationToken, now)).GuestAccessToken!;

        client.DefaultRequestHeaders.Authorization = new("Bearer", tokenA);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/guest/orders/{a.OrderId}")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync($"/guest/orders/{b.OrderId}")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync($"/guest/orders/{b.OrderId}/menu")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync($"/guest/orders/{b.OrderId}/dishes", new ProposeGuestDishRequest("salad", "B", 3))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync($"/guest/orders/{b.OrderId}/reduce", new ReduceGuestOrderRequest(line.Key, 0))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync($"/guest/orders/{b.OrderId}/request-confirmation", new { })).StatusCode);
        client.DefaultRequestHeaders.Authorization = new("Bearer", tokenB);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/guest/orders/{b.OrderId}")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync($"/guest/orders/{a.OrderId}")).StatusCode);
        var unchanged = (await client.GetFromJsonAsync<GuestOrderView>($"/guest/orders/{b.OrderId}"))!;
        Assert.Equal(2, Assert.Single(unchanged.Confirmed).Quantity);
        Assert.Equal(2, factory.GuestMailer.Messages.Count);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync($"/guest/orders/{b.OrderId}/reduce", new ReduceGuestOrderRequest(line.Key, 1))).StatusCode);
        Assert.Equal(1, Assert.Single((await client.GetFromJsonAsync<GuestOrderView>($"/guest/orders/{b.OrderId}"))!.Confirmed).Quantity);
        client.DefaultRequestHeaders.Authorization = new("Bearer", b.DraftAccessToken);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync($"/guest/orders/{b.OrderId}")).StatusCode);
    }

    [Fact]
    public async Task OwnerRemovesConfirmedGuestBeforeSubmittingAndWaiterSeesOnlyRemainingConfirmedFood()
    {
        using var factory = new ApiFactory(); using var client = factory.CreateClient();
        var now = DateTimeOffset.UtcNow;
        client.DefaultRequestHeaders.Authorization = new("Bearer", factory.SignedToken("owner", verifiedEmail: true));
        var bookingResponse = await client.PostAsJsonAsync("/reservations", new CreateBookingRequest("table-7", now.AddHours(2), now.AddHours(3), 4));
        Assert.Equal(HttpStatusCode.Created, bookingResponse.StatusCode);
        var booking = (await bookingResponse.Content.ReadFromJsonAsync<CreatedBooking>())!;
        var groups = factory.Services.GetRequiredService<IGroupRepository>();
        var groupId = (await groups.FindGroupByReservationAsync(booking.Id, default))!;
        var statusPath = $"/restaurant/reservations/{booking.Id}/status";
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync(statusPath, new StaffStatusChange(2))).StatusCode);
        client.DefaultRequestHeaders.Authorization = null;
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync(statusPath, new StaffStatusChange(2))).StatusCode);
        client.DefaultRequestHeaders.Authorization = new("Bearer", factory.SignedToken("foreign-waiter"));
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync(statusPath, new StaffStatusChange(2))).StatusCode);
        client.DefaultRequestHeaders.Authorization = new("Bearer", factory.SignedToken("restaurant-waiter"));
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync(statusPath, new StaffStatusChange(2))).StatusCode);
        client.DefaultRequestHeaders.Authorization = new("Bearer", factory.SignedToken("owner", verifiedEmail: true));
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync($"/owner/groups/{groupId}/own-order/dishes", new OwnerLineChange("salad", "Owner", 1))).StatusCode);
        var service = factory.Services.GetRequiredService<GuestOrderService>();
        var first = await service.CreateDraftAsync(groupId, "Guest A", "a@example.test", now.AddHours(1), now, TimeSpan.FromMinutes(15));
        var removed = await service.CreateDraftAsync(groupId, "Guest B", "b@example.test", now.AddHours(1), now, TimeSpan.FromMinutes(15));
        var pending = await service.CreateDraftAsync(groupId, "Guest C", "c@example.test", now.AddHours(1), now, TimeSpan.FromMinutes(15));
        await service.ProposeAsync(first.OrderId, first.DraftAccessToken, new("salad:A", "salad", "Salad", "A", 10m, 2), now);
        await service.ProposeAsync(removed.OrderId, removed.DraftAccessToken, new("salad:B", "salad", "Salad", "B", 10m, 1), now);
        await service.ProposeAsync(pending.OrderId, pending.DraftAccessToken, new("dessert:C", "dessert", "Dessert", "C", 8m, 1), now);
        foreach (var draft in new[] { first, removed })
        {
            await service.RequestConfirmationAsync(draft.OrderId, draft.DraftAccessToken, now);
            var message = factory.GuestMailer.Messages.Last();
            await service.ConfirmAsync(draft.OrderId, message.ConfirmationToken, now);
        }
        var before = (await client.GetFromJsonAsync<GroupSnapshot>($"/guest/groups/{groupId}/sessions"))!;
        Assert.Contains(before.Participants, item => item.Id == removed.OrderId && item.IsSubmitted);
        Assert.Contains(before.Participants, item => item.Id == pending.OrderId && !item.IsSubmitted);
        var deletion = await client.DeleteAsync($"/guest/groups/{groupId}/sessions/{removed.OrderId}");
        Assert.Equal(HttpStatusCode.OK, deletion.StatusCode);
        var after = (await deletion.Content.ReadFromJsonAsync<GroupSnapshot>())!;
        Assert.DoesNotContain(after.Participants, item => item.Id == removed.OrderId);
        Assert.Equal(before.Total - 10m, after.Total);
        var submit = await client.PostAsJsonAsync($"/group-reservations/{groupId}/submit", new SubmitRequest(booking.Id, after.Version));
        Assert.Equal(HttpStatusCode.OK, submit.StatusCode);
        Assert.Equal(3, Assert.Single((await submit.Content.ReadFromJsonAsync<SubmittedDish[]>())!).Quantity);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync($"/group-reservations/{groupId}/submit", new SubmitRequest(booking.Id, after.Version))).StatusCode);
        client.DefaultRequestHeaders.Authorization = new("Bearer", factory.SignedToken("restaurant-waiter"));
        var receipt = (await client.GetFromJsonAsync<RestaurantOrderReceipt>($"/restaurant/reservations/{booking.Id}/pre-order"))!;
        var dish = Assert.Single(receipt.Items);
        Assert.Equal("salad", dish.DishId); Assert.Equal(3, dish.Quantity); Assert.Equal(30m, receipt.Total);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync(statusPath, new StaffStatusChange(3))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync(statusPath, new StaffStatusChange(2))).StatusCode);
        Assert.Equal(2, (await client.GetFromJsonAsync<CreatedBooking[]>("/restaurant/reservations"))!.Single(item => item.Id == booking.Id).Status);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync(statusPath, new StaffStatusChange(2))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync(statusPath, new StaffStatusChange(3))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync(statusPath, new StaffStatusChange(2))).StatusCode);
        client.DefaultRequestHeaders.Authorization = new("Bearer", factory.SignedToken("owner", verifiedEmail: true));
        Assert.Equal(3, (await client.GetFromJsonAsync<CreatedBooking[]>("/reservations"))!.Single(item => item.Id == booking.Id).Status);
    }

    [Fact]
    public async Task OwnerChangeEndpointsRequireAuthenticationOwnershipAndVerifiedEmail()
    {
        using var factory = new ApiFactory(); using var client = factory.CreateClient();
        const string route = "/owner/groups/workshop-group/orders/unknown/clear";
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync(route, new { })).StatusCode);
        client.DefaultRequestHeaders.Authorization = new("Bearer", factory.SignedToken("foreign-owner", verifiedEmail: true));
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync(route, new { })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/owner/groups/workshop-group/history")).StatusCode);
        client.DefaultRequestHeaders.Authorization = new("Bearer", factory.SignedToken("workshop-owner-sub"));
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync(route, new { })).StatusCode);
        client.DefaultRequestHeaders.Authorization = new("Bearer", factory.SignedToken("workshop-owner-sub", verifiedEmail: true));
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsJsonAsync(route, new { })).StatusCode);
        var history = await client.GetAsync("/owner/groups/workshop-group/history");
        Assert.Equal(HttpStatusCode.OK, history.StatusCode);
        Assert.Contains("no-store", history.Headers.CacheControl!.ToString());
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/owner/groups/workshop-group/menu")).StatusCode);
    }

    [Fact]
    public async Task RemovingNewGuestThroughThePublicOwnerEndpointQueuesNoticesAndRevokesDraftAccess()
    {
        using var factory = new ApiFactory(); using var client = factory.CreateClient();
        var guest = factory.Services.GetRequiredService<GuestOrderService>(); var now = DateTimeOffset.UtcNow;
        var draft = await guest.CreateDraftAsync("workshop-group", "Guest", "guest@example.test", now.AddHours(1), now, TimeSpan.FromMinutes(15));
        client.DefaultRequestHeaders.Authorization = new("Bearer", factory.SignedToken("workshop-owner-sub", verifiedEmail: true));
        var response = await client.DeleteAsync("/guest/groups/workshop-group/sessions/" + draft.OrderId);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var snapshot = (await response.Content.ReadFromJsonAsync<GroupSnapshot>())!;
        Assert.NotNull(snapshot.NotificationId); Assert.DoesNotContain(snapshot.Participants, p => p.Id == draft.OrderId);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => guest.GetAsync(draft.OrderId, draft.DraftAccessToken));
    }

    [Fact]
    public async Task RestaurantReceiptEndpointRequiresAssignedStaffAndReturnsSubmittedItems()
    {
        using var factory = new ApiFactory(); using var client = factory.CreateClient();
        const string path = "/restaurant/reservations/workshop-reservation/pre-order";
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(path)).StatusCode);
        client.DefaultRequestHeaders.Authorization = new("Bearer", factory.SignedToken("workshop-owner-sub"));
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync(path)).StatusCode);
        await factory.Services.GetRequiredService<GroupService>().SubmitAsync("workshop-group", "workshop-reservation", "workshop-owner-sub");
        client.DefaultRequestHeaders.Authorization = new("Bearer", factory.SignedToken("restaurant-waiter"));
        var response = await client.GetAsync(path); Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("no-store", response.Headers.CacheControl!.ToString());
        Assert.Equal(50.5m, (await response.Content.ReadFromJsonAsync<RestaurantOrderReceipt>())!.Total);
    }

    [Fact]
    public async Task PublicBookingDiscoveryUsesServerCatalogAndReflectsExistingReservation()
    {
        using var factory = new ApiFactory(); using var client = factory.CreateClient(); var start = DateTimeOffset.UtcNow.AddHours(2);
        var query = "?startTime=" + Uri.EscapeDataString(start.ToString("O"));
        var locations = await client.GetFromJsonAsync<LocationListing[]>("/locations");
        Assert.Equal("workshop-location", Assert.Single(locations!).Id);
        var tables = await client.GetFromJsonAsync<AvailableTable[]>("/available-tables" + query + "&capacity=2&locationId=workshop-location");
        Assert.Equal("table-7", Assert.Single(tables!).Id);
        var slots = await client.GetFromJsonAsync<AvailableTimeRange[]>("/tables/table-7/available-time-slots" + query);
        var slot = Assert.Single(slots!); Assert.Equal(TimeSpan.FromMinutes(90), slot.End - slot.Start);
        client.DefaultRequestHeaders.Authorization = new("Bearer", factory.SignedToken("owner", verifiedEmail: true));
        Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync("/reservations", new CreateBookingRequest("table-7", start, start.AddMinutes(90), 2))).StatusCode);
        client.DefaultRequestHeaders.Authorization = null;
        Assert.Empty((await client.GetFromJsonAsync<AvailableTable[]>("/available-tables" + query + "&capacity=2"))!);
        Assert.Empty((await client.GetFromJsonAsync<AvailableTimeRange[]>("/tables/table-7/available-time-slots" + query))!);
        Assert.Empty((await client.GetFromJsonAsync<AvailableTable[]>("/available-tables" + query + "&capacity=5"))!);
    }

    [Fact]
    public async Task StaffBookingListRejectsAnonymousAndNonStaffAndShowsAssignedLocation()
    {
        using var factory = new ApiFactory(); using var client = factory.CreateClient(); var now = DateTimeOffset.UtcNow;
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/restaurant/reservations")).StatusCode);
        client.DefaultRequestHeaders.Authorization = new("Bearer", factory.SignedToken("owner", verifiedEmail: true));
        var request = new CreateBookingRequest("table-7", now.AddHours(2), now.AddHours(3), 2);
        var booking = (await (await client.PostAsJsonAsync("/reservations", request)).Content.ReadFromJsonAsync<CreatedBooking>())!;
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/restaurant/reservations")).StatusCode);
        client.DefaultRequestHeaders.Authorization = new("Bearer", factory.SignedToken("restaurant-waiter"));
        var response = await client.GetAsync("/restaurant/reservations"); Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("no-store", response.Headers.CacheControl!.ToString());
        Assert.Contains((await response.Content.ReadFromJsonAsync<CreatedBooking[]>())!, item => item.Id == booking.Id && item.CustomerId == "");
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/waiters/another/location")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/waiters/restaurant-waiter/location")).StatusCode);
    }

    [Fact]
    public async Task OwnerCanCancelBookingAndFreeItsTableThroughHttp()
    {
        using var factory = new ApiFactory(); using var client = factory.CreateClient(); var now = DateTimeOffset.UtcNow;
        var request = new CreateBookingRequest("table-7", now.AddHours(2), now.AddHours(3), 2);
        client.DefaultRequestHeaders.Authorization = new("Bearer", factory.SignedToken("owner", verifiedEmail: true));
        var created = (await (await client.PostAsJsonAsync("/reservations", request)).Content.ReadFromJsonAsync<CreatedBooking>())!;
        using var delete = new HttpRequestMessage(HttpMethod.Delete, "/reservations") { Content = JsonContent.Create(new CancelBookingRequest(created.Id)) };
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(delete)).StatusCode);
        Assert.Equal(1, Assert.Single((await client.GetFromJsonAsync<CreatedBooking[]>("/reservations"))!).Status);
        Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync("/reservations", request)).StatusCode);
    }

    [Fact]
    public async Task BookingRequiresVerifiedOwnerAndCreatesInvitationWithCorrectOwnership()
    {
        using var factory = new ApiFactory(); using var client = factory.CreateClient(); var now = DateTimeOffset.UtcNow;
        var request = new CreateBookingRequest("table-7", now.AddHours(2), now.AddHours(3), 2);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/reservations", request)).StatusCode);
        client.DefaultRequestHeaders.Authorization = new("Bearer", factory.SignedToken("owner"));
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/reservations", request)).StatusCode);
        client.DefaultRequestHeaders.Authorization = new("Bearer", factory.SignedToken("owner", verifiedEmail: true));
        var response = await client.PostAsJsonAsync("/reservations", request); Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var booking = (await response.Content.ReadFromJsonAsync<CreatedBooking>())!;
        var ownList = await client.GetFromJsonAsync<CreatedBooking[]>("/reservations");
        Assert.Contains(ownList!, item => item.Id == booking.Id);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/reservations/" + booking.Id + "/invite-token")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync("/reservations", request)).StatusCode);
        client.DefaultRequestHeaders.Authorization = new("Bearer", factory.SignedToken("other-owner", verifiedEmail: true));
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/reservations/" + booking.Id + "/invite-token")).StatusCode);
        Assert.DoesNotContain((await client.GetFromJsonAsync<CreatedBooking[]>("/reservations"))!, item => item.Id == booking.Id);
    }

    [Fact]
    public async Task CurrentUserUsesVerifiedIdentityAndServerAssignedRole()
    {
        using var factory = new ApiFactory(); using var client = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/me")).StatusCode);
        client.DefaultRequestHeaders.Authorization = new("Bearer", factory.SignedToken("restaurant-waiter"));
        var response = await client.GetAsync("/me");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode); Assert.Contains("no-store", response.Headers.CacheControl!.ToString());
        var user = (await response.Content.ReadFromJsonAsync<CurrentUser>())!;
        Assert.Equal("restaurant-waiter", user.Id); Assert.Equal("Waiter", user.Role);
        client.DefaultRequestHeaders.Authorization = new("Bearer", factory.SignedToken("ordinary-guest"));
        Assert.Equal("Customer", (await client.GetFromJsonAsync<CurrentUser>("/me"))!.Role);
    }

    [Fact]
    public async Task ProtectedEndpointRejectsAnonymousCaller()
    {
        using var factory = new ApiFactory();
        using var client = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/guest/groups/workshop-group/sessions")).StatusCode);
    }

    [Fact]
    public async Task UnsignedDemoTokenIsRejected()
    {
        using var factory = new ApiFactory();
        using var client = factory.CreateClient();
        var unsigned = new JwtSecurityToken("green-and-tasty-demo", "workshop-api", [new Claim("sub", "workshop-owner-sub")],
            expires: DateTime.UtcNow.AddMinutes(5));
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", new JwtSecurityTokenHandler().WriteToken(unsigned));
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/guest/groups/workshop-group/sessions")).StatusCode);
    }

    [Fact]
    public async Task SignedForeignOwnerGets403()
    {
        using var factory = new ApiFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", factory.SignedToken("other-user"));
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/guest/groups/workshop-group/sessions")).StatusCode);
    }

    [Fact]
    public async Task SignedOwnerCanReadSubmitAndObserveUpdatedStatus()
    {
        using var factory = new ApiFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", factory.SignedToken("workshop-owner-sub"));
        var snapshot = await client.GetFromJsonAsync<GroupSnapshot>("/guest/groups/workshop-group/sessions");
        Assert.Equal(65m, snapshot!.Total);
        var response = await client.PostAsJsonAsync("/group-reservations/workshop-group/submit", new SubmitRequest("workshop-reservation", snapshot.Version));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var items = await response.Content.ReadFromJsonAsync<SubmittedDish[]>();
        Assert.Equal(3, items!.Single(item => item.DishId == "dish-salad").Quantity);
        Assert.Equal("Submitted", (await client.GetFromJsonAsync<GroupStatus>("/guest/groups/workshop-group/status"))!.Status);
        var invite = await client.GetAsync("/guest/invites/workshop-public-invite");
        Assert.Equal(HttpStatusCode.OK, invite.StatusCode);
        Assert.Contains("\"GroupReservationId\"", await invite.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task CognitoIdTokenIsAccepted()
    {
        using var factory = new ApiFactory(useCognitoIssuer: true);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", factory.SignedToken("workshop-owner-sub", "id"));
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/guest/groups/workshop-group/sessions")).StatusCode);
    }

    [Theory]
    [InlineData("access")]
    [InlineData("refresh")]
    [InlineData("")]
    [InlineData(null)]
    public async Task CognitoNonIdOrMissingTokenUseIsRejected(string? tokenUse)
    {
        using var factory = new ApiFactory(useCognitoIssuer: true);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", factory.SignedToken("workshop-owner-sub", tokenUse));
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/guest/groups/workshop-group/sessions")).StatusCode);
    }

    [Fact]
    public async Task DeleteRejectsAnonymousCaller()
    {
        using var factory = new ApiFactory();
        using var client = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.DeleteAsync("/guest/groups/workshop-group/sessions/workshop-guest-session")).StatusCode);
    }

    [Fact]
    public async Task DeleteRejectsAuthenticatedNonOwnerWithoutChangingSnapshot()
    {
        using var factory = new ApiFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", factory.SignedToken("another-owner"));
        Assert.Equal(HttpStatusCode.Forbidden, (await client.DeleteAsync("/guest/groups/workshop-group/sessions/workshop-guest-session")).StatusCode);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", factory.SignedToken("workshop-owner-sub"));
        Assert.Equal(3, (await client.GetFromJsonAsync<GroupSnapshot>("/guest/groups/workshop-group/sessions"))!.Participants.Length);
    }

    [Fact]
    public async Task DeleteMissingSessionReturns404AndReadableError()
    {
        using var factory = new ApiFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", factory.SignedToken("workshop-owner-sub"));
        var response = await client.DeleteAsync("/guest/groups/workshop-group/sessions/missing");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Contains("Guest session not found.", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task DeleteForeignGroupSessionReturns400WithoutDeletingEitherGroup()
    {
        using var factory = new ApiFactory(includeForeignGroup: true);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", factory.SignedToken("workshop-owner-sub"));
        var response = await client.DeleteAsync("/guest/groups/workshop-group/sessions/foreign-session");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(3, (await client.GetFromJsonAsync<GroupSnapshot>("/guest/groups/workshop-group/sessions"))!.Participants.Length);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", factory.SignedToken("foreign-owner"));
        Assert.Single((await client.GetFromJsonAsync<GroupSnapshot>("/guest/groups/foreign-group/sessions"))!.Participants);
    }

    [Fact]
    public async Task DeleteOwnGroupGuestReturnsUpdatedSnapshot()
    {
        using var factory = new ApiFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", factory.SignedToken("workshop-owner-sub"));
        var response = await client.DeleteAsync("/guest/groups/workshop-group/sessions/workshop-guest-session");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var snapshot = await response.Content.ReadFromJsonAsync<GroupSnapshot>();
        Assert.Equal(2, snapshot!.Participants.Length);
        Assert.Equal(41m, snapshot.Total);
    }
}

