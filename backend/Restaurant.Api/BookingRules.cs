namespace Restaurant.Api;

public sealed record BookingTable(string Id, string LocationId, int Number, int Capacity, bool Available = true);
public sealed record BookingInterval(string TableId, DateTimeOffset Start, DateTimeOffset End);
public sealed record CreateBookingRequest(string TableId, DateTimeOffset StartTime, DateTimeOffset EndTime, int GuestCount);

public static class BookingRules
{
    // The caller must protect the overlap check and creation with the same atomic write.
    public static void Validate(CreateBookingRequest request, BookingTable? table, IEnumerable<BookingInterval> existing, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(request.TableId) || table is null || table.Id != request.TableId || !table.Available)
            throw new ApiException(400, "Table is not available.");
        if (request.GuestCount < 1 || request.GuestCount > table.Capacity)
            throw new ApiException(400, "Guest count exceeds table capacity.");
        if (request.StartTime <= now || request.EndTime <= request.StartTime)
            throw new ApiException(400, "Reservation time is invalid.");
        if (existing.Any(item => item.TableId == table.Id && request.StartTime < item.End && item.Start < request.EndTime))
            throw new ApiException(409, "The table has an overlapping reservation.");
    }
}
