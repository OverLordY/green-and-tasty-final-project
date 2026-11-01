using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace Restaurant.Api.Tests;

public sealed class GuestOrdersHttpTests
{
    private sealed class Clock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(2026, 10, 28, 14, 0, 0, TimeSpan.Zero);
    }
    private sealed class Mailer : IGuestConfirmationMailer, IGuestAccessMailer
    {
        public GuestConfirmationMail? Message { get; private set; }
        public bool Fail { get; set; }
        public GuestAccessMail? AccessMessage { get; private set; }
        public Task SendAsync(GuestAccessMail message, CancellationToken cancellationToken)
        {
            if (Fail) throw new GuestMailUnavailableException();
            AccessMessage = message; return Task.CompletedTask;
        }
        public Task SendAsync(GuestConfirmationMail message, CancellationToken cancellationToken)
        {
            if (Fail) throw new GuestMailUnavailableException();
            Message = message; return Task.CompletedTask;
        }
    }
    private sealed class Factory : WebApplicationFactory<Program>
    {
        public Mailer Mail { get; } = new();
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development")
                .UseSetting("Authentication:Authority", "https://test-issuer.example")
                .UseSetting("Authentication:Audience", "test")
                .UseSetting("Storage:UseInMemory", "true")
                .UseSetting("Restaurant:Menu:0:Id", "salad")
                .UseSetting("Restaurant:Menu:0:Name", "Salad")
                .UseSetting("Restaurant:Menu:0:Price", "12")
                .UseSetting("Restaurant:Menu:0:Available", "true")
                .UseSetting("Restaurant:Menu:1:Id", "other-dish")
                .UseSetting("Restaurant:Menu:1:Name", "Other location dish")
                .UseSetting("Restaurant:Menu:1:Price", "10")
                .UseSetting("Restaurant:Menu:1:Available", "true")
                .UseSetting("Restaurant:Menu:1:LocationId", "other-location");
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<TimeProvider>(); services.AddSingleton<TimeProvider>(new Clock());
                services.RemoveAll<IGuestConfirmationMailer>(); services.AddSingleton<IGuestConfirmationMailer>(Mail);
                services.RemoveAll<IGuestAccessMailer>(); services.AddSingleton<IGuestAccessMailer>(Mail);
            });
        }
    }
    private static async Task<GuestDraft> Create(HttpClient client)
    {
        var response = await client.PostAsJsonAsync("/guest/orders/", new CreateGuestOrderRequest(
            "workshop-public-invite", "Andrii", "andrii@example.test"));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Contains("no-store", response.Headers.CacheControl!.ToString());
        return (await response.Content.ReadFromJsonAsync<GuestDraft>())!;
    }

    [Fact]
    public async Task RecoveryUsesExplicitPostKeepsConfirmedTotalAndAllowsNewRecipientDishesFromPersonalMenu()
    {
        using var factory = new Factory(); using var client = factory.CreateClient(); var draft = await Create(client);
        var path = "/guest/orders/" + draft.OrderId;
        client.DefaultRequestHeaders.Authorization = new("Bearer", draft.DraftAccessToken);
        await client.PostAsJsonAsync(path + "/dishes", new ProposeGuestDishRequest("salad", "Olena", 2));
        await client.PostAsJsonAsync(path + "/request-confirmation", new { });
        var confirmed = await client.PostAsJsonAsync(path + "/confirm", new ConfirmGuestOrderRequest(factory.Mail.Message!.ConfirmationToken));
        var old = (await confirmed.Content.ReadFromJsonAsync<ConfirmationReceipt>())!.GuestAccessToken!;
        client.DefaultRequestHeaders.Authorization = null;
        var request = await client.PostAsJsonAsync(path + "/request-access", new GuestAccessEmailRequest("andrii@example.test"));
        Assert.Equal(HttpStatusCode.Accepted, request.StatusCode); Assert.DoesNotContain(draft.OrderId, await request.Content.ReadAsStringAsync());
        var link = Assert.Single(factory.Mail.AccessMessage!.Links);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(path + "/recover-access")).StatusCode);
        client.DefaultRequestHeaders.Authorization = new("Bearer", old);
        Assert.Equal(24m, (await client.GetFromJsonAsync<GuestOrderView>(path))!.ConfirmedTotal);
        client.DefaultRequestHeaders.Authorization = null;
        var recovered = await client.PostAsJsonAsync(path + "/recover-access", new ConfirmGuestOrderRequest(link.Token));
        Assert.Equal(HttpStatusCode.OK, recovered.StatusCode);
        var access = (await recovered.Content.ReadFromJsonAsync<ConfirmationReceipt>())!.GuestAccessToken!;
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync(path + "/recover-access", new ConfirmGuestOrderRequest(link.Token))).StatusCode);
        client.DefaultRequestHeaders.Authorization = new("Bearer", old);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(path + "/menu")).StatusCode);
        client.DefaultRequestHeaders.Authorization = new("Bearer", access);
        var menu = (await client.GetFromJsonAsync<GuestPersonalMenu>(path + "/menu"))!;
        Assert.True(menu.CanChange); Assert.Equal(12m, Assert.Single(menu.Items).Price);
        var added = await client.PostAsJsonAsync(path + "/dishes", new ProposeGuestDishRequest("salad", "Ira", 1));
        var view = (await added.Content.ReadFromJsonAsync<GuestOrderView>())!;
        Assert.Equal(24m, view.ConfirmedTotal); Assert.Equal(2, view.Pending!.Length);
        Assert.Contains(view.Pending, l => l.ForWhom == "Ira" && l.Quantity == 1);
        Assert.NotNull(view.ChangeDeadline); Assert.True(view.CanChange);
    }

    [Fact]
    public async Task PublicRecoveryResponseDoesNotRevealEmailMatchesCooldownOrMailFailure()
    {
        using var factory = new Factory(); using var client = factory.CreateClient(); var draft = await Create(client);
        var path = "/guest/orders/" + draft.OrderId + "/request-access";
        var unknown = await client.PostAsJsonAsync(path, new GuestAccessEmailRequest("unknown@example.test"));
        factory.Mail.Fail = true;
        var failure = await client.PostAsJsonAsync(path, new GuestAccessEmailRequest("andrii@example.test"));
        var cooldown = await client.PostAsJsonAsync(path, new GuestAccessEmailRequest("andrii@example.test"));
        var missing = await client.PostAsJsonAsync("/guest/orders/unknown/request-access", new GuestAccessEmailRequest("andrii@example.test"));
        var responses = new[] { unknown, failure, cooldown, missing };
        Assert.All(responses, response => Assert.Equal(HttpStatusCode.Accepted, response.StatusCode));
        var expected = await unknown.Content.ReadAsStringAsync();
        foreach (var response in responses) Assert.Equal(expected, await response.Content.ReadAsStringAsync());
        Assert.Null(factory.Mail.AccessMessage);
    }

    [Fact]
    public async Task InvitationRecoverySendsMatchingOrdersAndAnonymousMenuAccessIsRejected()
    {
        using var factory = new Factory(); using var client = factory.CreateClient(); var draft = await Create(client);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/guest/orders/" + draft.OrderId + "/menu")).StatusCode);
        var response = await client.PostAsJsonAsync("/guest/orders/recover-access", new GuestInviteAccessRequest("workshop-public-invite", "andrii@example.test"));
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal(draft.OrderId, Assert.Single(factory.Mail.AccessMessage!.Links).OrderId);
        Assert.Contains("no-store", response.Headers.CacheControl!.ToString());
    }

    [Fact]
    public async Task DishesFromAnotherLocationAreNotShownOrAccepted()
    {
        using var factory = new Factory(); using var client = factory.CreateClient(); var draft = await Create(client);
        var preview = (await (await client.PostAsJsonAsync("/guest/orders/preview", new GuestMenuRequest("workshop-public-invite")))
            .Content.ReadFromJsonAsync<GuestMenuPreview>())!;
        Assert.DoesNotContain(preview.Items, item => item.Id == "other-dish");
        client.DefaultRequestHeaders.Authorization = new("Bearer", draft.DraftAccessToken);
        var menu = (await client.GetFromJsonAsync<GuestPersonalMenu>("/guest/orders/" + draft.OrderId + "/menu"))!;
        Assert.DoesNotContain(menu.Items, item => item.Id == "other-dish");
        var response = await client.PostAsJsonAsync("/guest/orders/" + draft.OrderId + "/dishes", new ProposeGuestDishRequest("other-dish", "Guest", 1));
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task DraftCanBeCreatedAtTheCanonicalGatewayPathWithoutTrailingSlash()
    {
        using var factory = new Factory(); using var client = factory.CreateClient();
        var response = await client.PostAsJsonAsync("/guest/orders", new CreateGuestOrderRequest("workshop-public-invite", "Guest", "guest@example.test"));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task FullHttpConfirmationFlowRequiresExplicitPostAndRotatesDraftAccess()
    {
        using var factory = new Factory(); using var client = factory.CreateClient();
        var draft = await Create(client);
        var path = "/guest/orders/" + draft.OrderId;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", draft.DraftAccessToken);
        var add = await client.PostAsJsonAsync(path + "/dishes", new ProposeGuestDishRequest("salad", "Olena", 2));
        Assert.Equal(HttpStatusCode.OK, add.StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, (await client.PostAsJsonAsync(path + "/request-confirmation", new { })).StatusCode);
        var message = Assert.IsType<GuestConfirmationMail>(factory.Mail.Message);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(path + "/confirm")).StatusCode);
        var before = (await client.GetFromJsonAsync<GuestOrderView>(path))!;
        Assert.Equal(0, before.ConfirmedTotal);
        var confirmation = await client.PostAsJsonAsync(path + "/confirm", new ConfirmGuestOrderRequest(message.ConfirmationToken));
        Assert.Equal(HttpStatusCode.OK, confirmation.StatusCode);
        var access = (await confirmation.Content.ReadFromJsonAsync<ConfirmationReceipt>())!.GuestAccessToken!;
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(path)).StatusCode);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", access);
        var view = (await client.GetFromJsonAsync<GuestOrderView>(path))!;
        Assert.Equal(24m, view.ConfirmedTotal);
        var decrease = await client.PostAsJsonAsync(path + "/reduce", new ReduceGuestOrderRequest("salad:Olena", 1));
        var reduced = (await decrease.Content.ReadFromJsonAsync<GuestOrderView>())!;
        Assert.Equal(12m, reduced.ConfirmedTotal);
        Assert.Contains("зменшена до 1", Assert.Single(reduced.Notices).Message);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await client.PostAsJsonAsync(path + "/confirm", new ConfirmGuestOrderRequest(message.ConfirmationToken))).StatusCode);
    }

    [Fact]
    public async Task ForeignDraftCannotReadAnotherOrder()
    {
        using var factory = new Factory(); using var client = factory.CreateClient();
        var first = await Create(client); var second = await Create(client);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", second.DraftAccessToken);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/guest/orders/" + first.OrderId)).StatusCode);
    }

    [Fact]
    public async Task UnknownInvitationCannotCreateOrder()
    {
        using var factory = new Factory(); using var client = factory.CreateClient();
        var response = await client.PostAsJsonAsync("/guest/orders/", new CreateGuestOrderRequest("unknown", "A", "a@example.test"));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task PreviewShowsServerMenuAndDeadlinesWithoutCreatingAnOrder()
    {
        using var factory = new Factory(); using var client = factory.CreateClient();
        var response = await client.PostAsJsonAsync("/guest/orders/preview", new GuestMenuRequest("workshop-public-invite"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var menu = (await response.Content.ReadFromJsonAsync<GuestMenuPreview>())!;
        Assert.Equal(12m, Assert.Single(menu.Items).Price);
        Assert.True(menu.CanChange);
        Assert.Equal(TimeSpan.FromMinutes(15), menu.Deadlines.SubmissionCloses - menu.Deadlines.ChangesClose);
        var group = (await factory.Services.GetRequiredService<IGroupRepository>().GetGroupAsync("workshop-group", default))!;
        Assert.Null(group.GuestOrders);
    }

    [Fact]
    public async Task ClientCannotOverrideServerDishPrice()
    {
        using var factory = new Factory(); using var client = factory.CreateClient();
        var draft = await Create(client);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", draft.DraftAccessToken);
        var response = await client.PostAsJsonAsync("/guest/orders/" + draft.OrderId + "/dishes",
            new { dishId = "salad", forWhom = "Olena", quantity = 1, unitPrice = 0 });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var order = (await response.Content.ReadFromJsonAsync<GuestOrderView>())!;
        Assert.Equal(12m, Assert.Single(order.Pending!).UnitPrice);
    }

    [Fact]
    public async Task ClosedGroupRejectsFurtherDishChanges()
    {
        using var factory = new Factory(); using var client = factory.CreateClient();
        var draft = await Create(client);
        var groups = factory.Services.GetRequiredService<IGroupRepository>();
        var group = (await groups.GetGroupAsync("workshop-group", default))!;
        Assert.True(await groups.SaveAsync(group with { Status = "Submitted", Version = group.Version + 1 }, group.Version, null, default));
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", draft.DraftAccessToken);
        var response = await client.PostAsJsonAsync("/guest/orders/" + draft.OrderId + "/dishes",
            new ProposeGuestDishRequest("salad", "A", 1));
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task DeliveryFailureIs503AndDoesNotClaimEmailWasSent()
    {
        using var factory = new Factory(); using var client = factory.CreateClient();
        var draft = await Create(client); factory.Mail.Fail = true;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", draft.DraftAccessToken);
        var path = "/guest/orders/" + draft.OrderId;
        await client.PostAsJsonAsync(path + "/dishes", new ProposeGuestDishRequest("salad", "A", 1));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.PostAsJsonAsync(path + "/request-confirmation", new { })).StatusCode);
        Assert.Null(factory.Mail.Message);
    }
}
