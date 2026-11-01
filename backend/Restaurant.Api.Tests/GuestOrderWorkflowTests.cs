using Restaurant.Api;
using Xunit;

namespace Restaurant.Api.Tests;

public sealed class GuestOrderWorkflowTests
{
    private static readonly DateTimeOffset Now = new(2030, 1, 1, 17, 0, 0, TimeSpan.Zero);
    private static readonly GuestOrderLine Salad = new("salad-for-olena", "salad", "Salad", "Olena", 12m, 2);
    private static GuestOrderWorkflow Create() => new(Now.AddMinutes(30), TimeSpan.FromMinutes(15));
    private static (GuestOrderWorkflow Order, string AccessToken) Confirmed()
    {
        var order = Create();
        order.Propose(Salad, Now);
        var challenge = order.RequestConfirmation(Now);
        var access = order.Confirm(challenge.Token, Now.AddMinutes(1)).GuestAccessToken;
        return (order, Assert.IsType<string>(access));
    }

    [Fact] public void UnconfirmedItemsNeverCountTowardConfirmedTotal()
    {
        var order = Create(); order.Propose(Salad, Now);
        Assert.Equal(0, order.ConfirmedTotal);
        order.RequestConfirmation(Now);
        Assert.Equal(GuestOrderStatus.AwaitingEmail, order.Status);
    }

    [Fact] public void ConfirmationCreatesPersonalCredentialAndCannotBeReplayed()
    {
        var order = Create(); order.Propose(Salad, Now);
        var challenge = order.RequestConfirmation(Now);
        var access = Assert.IsType<string>(order.Confirm(challenge.Token, Now.AddMinutes(1)).GuestAccessToken);
        Assert.True(order.IsGuestAuthorized(access));
        Assert.False(order.IsGuestAuthorized(challenge.Token));
        Assert.False(order.IsGuestAuthorized("someone-else"));
        Assert.Throws<UnauthorizedAccessException>(() => order.Confirm(challenge.Token, Now.AddMinutes(2)));
        Assert.Equal(24m, order.ConfirmedTotal);
    }

    [Fact] public void PreviewValidatesTokenWithoutConfirmingOrConsumingIt()
    {
        var order = Create(); order.Propose(Salad, Now);
        var challenge = order.RequestConfirmation(Now);
        Assert.Throws<UnauthorizedAccessException>(() => order.ValidateConfirmation("incorrect", Now));
        order.ValidateConfirmation(challenge.Token, Now);
        order.ValidateConfirmation(challenge.Token, Now);
        Assert.Empty(order.Confirmed);
        Assert.Equal(2, Assert.Single(order.Pending!).Quantity);
        order.Confirm(challenge.Token, Now.AddMinutes(1));
        Assert.Throws<UnauthorizedAccessException>(() => order.ValidateConfirmation(challenge.Token, Now.AddMinutes(2)));
    }

    [Fact] public void PersonalCredentialDoesNotAuthorizeAnotherGuestsOrder()
    {
        var (first, firstToken) = Confirmed();
        var (second, secondToken) = Confirmed();
        Assert.True(first.IsGuestAuthorized(firstToken));
        Assert.False(first.IsGuestAuthorized(secondToken));
        Assert.False(second.IsGuestAuthorized(firstToken));
        Assert.True(second.IsGuestAuthorized(secondToken));
    }

    [Fact] public void AnIncreaseRemainsPendingUntilConfirmed()
    {
        var (order, oldAccess) = Confirmed();
        order.Propose(Salad with { Quantity = 3 }, Now.AddMinutes(2));
        Assert.Equal(24m, order.ConfirmedTotal);
        Assert.Equal(GuestOrderStatus.ChangesAwaitingConfirmation, order.Status);
        var challenge = order.RequestConfirmation(Now.AddMinutes(3));
        var access = Assert.IsType<string>(order.Confirm(challenge.Token, Now.AddMinutes(4)).GuestAccessToken);
        Assert.True(order.IsGuestAuthorized(access));
        Assert.False(order.IsGuestAuthorized(oldAccess));
        Assert.Equal(36m, order.ConfirmedTotal);
    }

    [Fact] public void DecreaseIsImmediateAndVisible()
    {
        var (order, _) = Confirmed();
        order.Reduce(Salad.Key, 1, Now.AddMinutes(2));
        Assert.Equal(12m, order.ConfirmedTotal);
        Assert.Equal(GuestOrderStatus.Confirmed, order.Status);
        Assert.Equal("Кількість порцій зменшена до 1", Assert.Single(order.Notices).Message);
        order.Reduce(Salad.Key, 0, Now.AddMinutes(3));
        Assert.Empty(order.Confirmed);
        Assert.Equal("Страву скасовано", Assert.Single(order.Notices).Message);
        Assert.Equal(GuestOrderStatus.Choosing, order.Status);
    }

    [Fact] public void ReducingPendingIncreaseDoesNotIncreaseConfirmedAmount()
    {
        var (order, _) = Confirmed();
        order.Propose(Salad with { Quantity = 4 }, Now.AddMinutes(2));
        order.Reduce(Salad.Key, 3, Now.AddMinutes(3));
        Assert.Equal(24m, order.ConfirmedTotal);
        Assert.Equal(3, Assert.Single(order.Pending!).Quantity);
    }

    [Fact] public void EditingInvalidatesEarlierConfirmation()
    {
        var (order, _) = Confirmed();
        order.Propose(Salad with { Quantity = 3 }, Now.AddMinutes(2));
        var stale = order.RequestConfirmation(Now.AddMinutes(3));
        order.Propose(Salad with { Quantity = 4 }, Now.AddMinutes(4));
        Assert.Throws<UnauthorizedAccessException>(() => order.Confirm(stale.Token, Now.AddMinutes(5)));
        Assert.Equal(24m, order.ConfirmedTotal);
    }

    [Fact] public void DeadlineAndExpiryAreStrict()
    {
        var order = Create(); order.Propose(Salad, Now);
        var challenge = order.RequestConfirmation(Now);
        Assert.Throws<UnauthorizedAccessException>(() => order.Confirm(challenge.Token, Now.AddMinutes(15)));
        Assert.Throws<InvalidOperationException>(() => order.Propose(Salad, Now.AddMinutes(30)));
        Assert.Throws<InvalidOperationException>(() => order.Confirm(challenge.Token, Now.AddMinutes(30)));
        Assert.Equal(0, order.ConfirmedTotal);
    }

    [Fact] public void ReissueInvalidatesPreviousConfirmationAndCannotExceedDeadline()
    {
        var order = Create(); order.Propose(Salad, Now);
        var first = order.RequestConfirmation(Now);
        var second = order.RequestConfirmation(Now.AddMinutes(20));
        Assert.Equal(Now.AddMinutes(30), second.ExpiresAt);
        Assert.Throws<UnauthorizedAccessException>(() => order.Confirm(first.Token, Now.AddMinutes(21)));
        order.Confirm(second.Token, Now.AddMinutes(21));
        Assert.Equal(24m, order.ConfirmedTotal);
    }

    [Fact] public void RestaurantPreparationAndReviewTimeSetBothDeadlines()
    {
        var start = Now.AddHours(1);
        var limits = OrderDeadlines.FromReservation(start, TimeSpan.FromMinutes(30), TimeSpan.FromMinutes(15));
        Assert.Equal(Now.AddMinutes(15), limits.ChangesClose);
        Assert.Equal(Now.AddMinutes(30), limits.SubmissionCloses);
        Assert.False(limits.CanChange(limits.ChangesClose));
        Assert.False(limits.CanSubmit(limits.SubmissionCloses));
    }
}
