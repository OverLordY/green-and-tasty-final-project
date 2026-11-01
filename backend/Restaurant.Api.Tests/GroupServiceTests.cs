using Restaurant.Api;
using Xunit;

namespace Restaurant.Api.Tests;

public sealed class GroupServiceTests
{
    private static (GroupService Service, InMemoryGroupRepository Repository) Setup(params GroupAggregate[] groups)
    {
        var repository = new InMemoryGroupRepository(groups.Length == 0 ? [DevelopmentFixture.Create()] : groups);
        return (new GroupService(repository), repository);
    }
    private const string Owner = "workshop-owner-sub";
    private const string Group = "workshop-group";
    private const string Reservation = "workshop-reservation";

    [Fact]
    public async Task SnapshotIncludesPendingCartAndDecimalTotals()
    {
        var (service, _) = Setup();
        var snapshot = await service.GetSessionsAsync(Group, Owner);
        Assert.Equal(3, snapshot.Participants.Length);
        Assert.Equal(65m, snapshot.Total);
        Assert.Equal(26.50m, snapshot.Participants[0].Total);
        Assert.False(snapshot.Participants[2].IsSubmitted);
    }

    [Fact]
    public async Task ReturnsOwnOwnerSessionId()
    {
        var (service, _) = Setup();
        Assert.Equal("workshop-owner-session", (await service.GetOwnerSessionAsync(Reservation, Owner)).GuestSessionId);
    }

    [Theory]
    [InlineData("", 401)]
    [InlineData("another-owner", 403)]
    public async Task RejectsMissingOrWrongOwnership(string subject, int expected)
    {
        var (service, _) = Setup();
        var error = await Assert.ThrowsAsync<ApiException>(() => service.GetSessionsAsync(Group, subject));
        Assert.Equal(expected, error.Status);
    }

    [Fact]
    public async Task MissingGroupReturns404()
    {
        var (service, _) = Setup();
        Assert.Equal(404, (await Assert.ThrowsAsync<ApiException>(() => service.GetSessionsAsync("missing", Owner))).Status);
    }

    [Fact]
    public async Task MissingReservationReturns404()
    {
        var (service, _) = Setup();
        Assert.Equal(404, (await Assert.ThrowsAsync<ApiException>(() => service.GetOwnerSessionAsync("missing", Owner))).Status);
        Assert.Equal(404, (await Assert.ThrowsAsync<ApiException>(() => service.SubmitAsync(Group, "missing", Owner))).Status);
    }

    [Fact]
    public async Task SubmitMergesSubmittedCartsAndChangesBothStatuses()
    {
        var (service, repository) = Setup();
        var items = await service.SubmitAsync(Group, Reservation, Owner);
        Assert.Equal(3, items.Single(item => item.DishId == "dish-salad").Quantity);
        Assert.Equal(1, items.Single(item => item.DishId == "dish-bowl").Quantity);
        var stored = await repository.GetGroupAsync(Group, default);
        Assert.Equal("Submitted", stored!.Status);
        Assert.Equal("SubmittedByCustomer", stored.OrderStatus);
        Assert.Equal(items, stored.OrderItems);
        Assert.Equal(1, stored.Version);
    }

    [Fact]
    public async Task RepeatedSubmitDoesNotDuplicateItems()
    {
        var (service, repository) = Setup();
        await service.SubmitAsync(Group, Reservation, Owner);
        Assert.Equal(400, (await Assert.ThrowsAsync<ApiException>(() => service.SubmitAsync(Group, Reservation, Owner))).Status);
        Assert.Equal(2, (await repository.GetGroupAsync(Group, default))!.OrderItems.Length);
    }

    [Fact]
    public async Task NoSubmittedSessionsReturns400WithoutChangingState()
    {
        var fixture = DevelopmentFixture.Create();
        var (service, repository) = Setup(fixture with { Participants = fixture.Participants.Select(person => person with { IsSubmitted = false }).ToArray() });
        Assert.Equal(400, (await Assert.ThrowsAsync<ApiException>(() => service.SubmitAsync(Group, Reservation, Owner))).Status);
        Assert.Equal("Open", (await repository.GetGroupAsync(Group, default))!.Status);
    }

    [Fact]
    public async Task NoSubmittedDishesReturns400()
    {
        var fixture = DevelopmentFixture.Create();
        var (service, _) = Setup(fixture with { Participants = fixture.Participants.Select(person => person with { Items = [] }).ToArray() });
        Assert.Equal(400, (await Assert.ThrowsAsync<ApiException>(() => service.SubmitAsync(Group, Reservation, Owner))).Status);
    }

    [Fact]
    public async Task MissingOrderReturns400()
    {
        var (service, _) = Setup(DevelopmentFixture.Create() with { OrderId = null });
        Assert.Equal(400, (await Assert.ThrowsAsync<ApiException>(() => service.SubmitAsync(Group, Reservation, Owner))).Status);
    }

    [Fact]
    public async Task WrongReservationGroupReturns400()
    {
        var fixture = DevelopmentFixture.Create();
        var (service, _) = Setup(fixture, fixture with { Id = "other-group", ReservationId = "other-reservation", Participants = [] });
        Assert.Equal(400, (await Assert.ThrowsAsync<ApiException>(() => service.SubmitAsync(Group, "other-reservation", Owner))).Status);
    }

    [Fact]
    public async Task DeleteReturnsUpdatedListAndTotal()
    {
        var (service, repository) = Setup();
        var snapshot = await service.RemoveAsync(Group, "workshop-guest-session", Owner);
        Assert.Equal(2, snapshot.Participants.Length);
        Assert.Equal(41m, snapshot.Total);
        Assert.DoesNotContain(snapshot.Participants, person => person.Id == "workshop-guest-session");
        Assert.Null(await repository.FindGroupBySessionAsync("workshop-guest-session", default));
    }

    [Fact]
    public async Task DeleteUnknownSessionReturns404()
    {
        var (service, _) = Setup();
        Assert.Equal(404, (await Assert.ThrowsAsync<ApiException>(() => service.RemoveAsync(Group, "missing", Owner))).Status);
    }

    [Fact]
    public async Task DeleteForeignGroupSessionReturns400()
    {
        var fixture = DevelopmentFixture.Create();
        var foreign = fixture with { Id = "foreign", ReservationId = "foreign-reservation", Participants = [fixture.Participants[1] with { Id = "foreign-session" }] };
        var (service, _) = Setup(fixture, foreign);
        Assert.Equal(400, (await Assert.ThrowsAsync<ApiException>(() => service.RemoveAsync(Group, "foreign-session", Owner))).Status);
    }

    [Fact]
    public async Task CannotDeleteOwner()
    {
        var (service, _) = Setup();
        Assert.Equal(400, (await Assert.ThrowsAsync<ApiException>(() => service.RemoveAsync(Group, "workshop-owner-session", Owner))).Status);
    }

    [Theory]
    [InlineData("Submitted")]
    [InlineData("Closed")]
    public async Task CannotMutateNonOpenGroup(string status)
    {
        var (service, _) = Setup(DevelopmentFixture.Create() with { Status = status });
        Assert.Equal(400, (await Assert.ThrowsAsync<ApiException>(() => service.SubmitAsync(Group, Reservation, Owner))).Status);
        Assert.Equal(400, (await Assert.ThrowsAsync<ApiException>(() => service.RemoveAsync(Group, "workshop-guest-session", Owner))).Status);
    }

    [Fact]
    public async Task OptimisticVersionRejectsStaleWrite()
    {
        var (_, repository) = Setup();
        var first = (await repository.GetGroupAsync(Group, default))!;
        var stale = (await repository.GetGroupAsync(Group, default))!;
        Assert.True(await repository.SaveAsync(first with { Version = 1, Status = "Submitted" }, 0, null, default));
        Assert.False(await repository.SaveAsync(stale with { Version = 1 }, 0, null, default));
        Assert.Equal("Submitted", (await repository.GetGroupAsync(Group, default))!.Status);
    }

    [Fact]
    public async Task RepositoryReturnsDetachedSnapshots()
    {
        var (_, repository) = Setup();
        var first = (await repository.GetGroupAsync(Group, default))!;
        first.Participants[0] = first.Participants[0] with { Name = "Changed outside repository" };
        Assert.Equal("Owner", (await repository.GetGroupAsync(Group, default))!.Participants[0].Name);
    }

    [Fact]
    public async Task StatusAndInviteHelpersMatchFrontendModels()
    {
        var (service, _) = Setup();
        var status = await service.GetStatusAsync(Group, Owner);
        Assert.Equal(new GroupStatus(3, 5, "Open"), status);
        var token = await service.GetInviteTokenAsync(Reservation, Owner);
        Assert.Equal(Group, (await service.GetInviteAsync(token)).GroupReservationId);
    }
}
