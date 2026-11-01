using Restaurant.Api;
using Xunit;

namespace Restaurant.Api.Tests;

public sealed class BookingRulesTests
{
    private static readonly DateTimeOffset Now = new(2030, 1, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly BookingTable Table = new("table", "location", 7, 4);
    private static readonly CreateBookingRequest Request = new("table", Now.AddHours(2), Now.AddHours(3), 3);
    [Fact]
    public void TouchingIntervalsAndAnotherTableAreAllowed()
    {
        BookingRules.Validate(Request, Table, [new("table", Now.AddHours(1), Request.StartTime),
            new("table", Request.EndTime, Now.AddHours(4)), new("other", Request.StartTime, Request.EndTime)], Now);
    }
    [Theory]
    [InlineData(90, 150)] [InlineData(150, 210)] [InlineData(130, 140)] [InlineData(60, 240)]
    public void OverlappingIntervalsAreRejected(int startMinutes, int endMinutes)
    {
        var exception = Assert.Throws<ApiException>(() => BookingRules.Validate(Request, Table,
            [new("table", Now.AddMinutes(startMinutes), Now.AddMinutes(endMinutes))], Now));
        Assert.Equal(409, exception.Status);
    }
    [Fact]
    public void InvalidTableCapacityAndTimeAreRejected()
    {
        Assert.Equal(400, Assert.Throws<ApiException>(() => BookingRules.Validate(Request, null, [], Now)).Status);
        Assert.Equal(400, Assert.Throws<ApiException>(() => BookingRules.Validate(Request, Table with { Available = false }, [], Now)).Status);
        Assert.Equal(400, Assert.Throws<ApiException>(() => BookingRules.Validate(Request with { GuestCount = 5 }, Table, [], Now)).Status);
        Assert.Equal(400, Assert.Throws<ApiException>(() => BookingRules.Validate(Request with { StartTime = Now }, Table, [], Now)).Status);
        Assert.Equal(400, Assert.Throws<ApiException>(() => BookingRules.Validate(Request with { EndTime = Request.StartTime }, Table, [], Now)).Status);
    }
}
