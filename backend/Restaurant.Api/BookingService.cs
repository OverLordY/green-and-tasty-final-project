using System.Security.Cryptography;

namespace Restaurant.Api;

public interface IBookingWriter
{
    Task<TableSchedule> ReadScheduleAsync(string tableId, CancellationToken ct = default);
    Task<bool> TryCreateAsync(GroupAggregate group, CreateBookingRequest request, BookingTable table, TableSchedule schedule, DateTimeOffset now, CancellationToken ct = default);
}
public interface IOwnerBookingReader
{
    Task<GroupAggregate[]> ListAsync(string subject, CancellationToken ct = default);
}
public interface ILocationBookingReader
{
    Task<GroupAggregate[]> ListLocationAsync(string locationId, CancellationToken ct = default);
}
public interface IBookingCancellation
{
    Task<bool> TryCancelAsync(GroupAggregate group, TableSchedule schedule, CancellationToken ct = default);
}
public interface IBookingRescheduler
{
    Task<bool> TryRescheduleAsync(GroupAggregate previous, GroupAggregate updated, TableSchedule schedule, CancellationToken ct = default);
}
public sealed record CancelBookingRequest(string Id);
public sealed record CancelBookingResult(string Message, string GroupId, string[] NotificationIds);
public sealed record EditBookingRequest(string ReservationId, DateTimeOffset StartTime, DateTimeOffset EndTime, int GuestCount);
public sealed record EditBookingResult(CreatedBooking Reservation, string GroupId, string[] NotificationIds);
public sealed record StaffStatusChange(int NewStatus);
public sealed record StaffStatusResult(string Id, int Status);
public sealed record BookingLocation(string Id, string RestaurantName, string Address);
public sealed record CreatedBooking(string Id, string TableId, string CustomerId, DateTimeOffset StartTime,
    DateTimeOffset EndTime, int GuestCount, int Status, string LocationAddress, int TableCapacity, string RestaurantName, string WaiterId,
    bool HasSubmittedPreOrder = false, string? TableName = null, string? CreatedByName = null, string? CreatedByRole = null,
    int? SubmittedDishCount = null);

public sealed class BookingService(IBookingWriter writer, IConfiguration config, TimeProvider clock)
{
    public async Task<EditBookingResult> EditAsync(EditBookingRequest request, string subject, IGroupRepository groups,
        IBookingRescheduler rescheduler, CancellationToken ct = default)
    {
        var id = await groups.FindGroupByReservationAsync(request.ReservationId, ct) ?? throw new ApiException(404, "Reservation not found.");
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var group = await groups.GetGroupAsync(id, ct) ?? throw new ApiException(404, "Reservation not found.");
            if (group.OwnerSubject != subject) throw new ApiException(403, "Only the owner may edit the reservation.");
            if (group.Status != "Open" || group.ReservationCancelled || group.TableId is null || string.IsNullOrWhiteSpace(group.OwnerEmail))
                throw new ApiException(409, "This reservation cannot be edited; contact the restaurant.");
            var now = clock.GetUtcNow();
            var oldStart = DateTimeOffset.Parse(group.Invite.StartTime, System.Globalization.CultureInfo.InvariantCulture);
            var oldEnd = DateTimeOffset.Parse(group.Invite.EndTime, System.Globalization.CultureInfo.InvariantCulture);
            var preparation = TimeSpan.FromMinutes(config.GetValue<int?>("Restaurant:PreparationMinutes") ?? 30);
            var review = TimeSpan.FromMinutes(config.GetValue<int?>("Restaurant:ReviewMinutes") ?? 15);
            if (!OrderDeadlines.FromReservation(oldStart, preparation, review).CanChange(now))
                throw new ApiException(409, "The reservation change deadline has passed.");
            if (request.StartTime <= now || request.EndTime <= request.StartTime)
                throw new ApiException(400, "Reservation time is invalid.");
            var deadlines = OrderDeadlines.FromReservation(request.StartTime, preparation, review);
            if (!deadlines.CanChange(now)) throw new ApiException(400, "The new reservation time is too close.");
            if (request.GuestCount < group.Participants.Length)
                throw new ApiException(409, "Guest count cannot be lower than the number of participants.");
            var table = (config.GetSection("Restaurant:Tables").Get<BookingTable[]>() ?? []).SingleOrDefault(item => item.Id == group.TableId);
            var schedule = await writer.ReadScheduleAsync(group.TableId, ct);
            var others = schedule.Intervals.Where(item => !(item.TableId == group.TableId && item.Start == oldStart && item.End == oldEnd)).ToArray();
            if (others.Length != schedule.Intervals.Length - 1) throw new ApiException(409, "Reservation schedule requires reconciliation.");
            BookingRules.Validate(new(group.TableId, request.StartTime, request.EndTime, request.GuestCount), table, others, now);
            if (request.StartTime == oldStart && request.EndTime == oldEnd && request.GuestCount == group.Invite.MaxGuestCount)
                return new((await ListAsyncForGroup(group, table!)), group.Id, []);
            var records = (group.GuestOrders?.Values ?? Enumerable.Empty<StoredGuestOrder>())
                .Where(record => group.Participants.Any(person => person.Id == record.Id && person.Type == "Guest")).ToArray();
            if ((group.Notifications?.Length ?? 0) + records.Length > 200 || (group.Audit?.Length ?? 0) + records.Length > 500)
                throw new ApiException(409, "Notification/history capacity reached.");
            var updatedRecords = new Dictionary<string, StoredGuestOrder>(group.GuestOrders ?? [], StringComparer.Ordinal);
            var notices = new List<OrderChangeNotification>(); var audit = new List<OrderAuditEntry>();
            var timeChanged = request.StartTime != oldStart || request.EndTime != oldEnd;
            foreach (var record in records)
            {
                var state = timeChanged
                    ? record.State with { ChangeDeadline = deadlines.ChangesClose, ChallengeHash = null, Revision = checked(record.State.Revision + 1) }
                    : record.State;
                updatedRecords[record.Id] = timeChanged ? record with { State = state, Version = checked(record.Version + 1) } : record;
                var summary = $"Організатор змінив бронювання. Час (UTC): {oldStart:u} – {oldEnd:u} → {request.StartTime:u} – {request.EndTime:u}; кількість гостей: {group.Invite.MaxGuestCount} → {request.GuestCount}.";
                notices.Add(new(Guid.NewGuid().ToString("N"), record.Id, record.Email, group.OwnerEmail, summary,
                    timeChanged && state.Pending is { Length: > 0 }, now, state.Revision,
                    timeChanged && state.Pending is { Length: > 0 } ? state.Pending : state.Confirmed, IsReservationChange: true));
                audit.Add(new(Guid.NewGuid().ToString("N"), subject, record.Id, "EditReservation", now, summary));
            }
            var invite = group.Invite with { StartTime = request.StartTime.ToString("O"), EndTime = request.EndTime.ToString("O"), MaxGuestCount = request.GuestCount };
            var updated = group with { Invite = invite, SubmissionDeadline = deadlines.SubmissionCloses, GuestOrders = updatedRecords,
                Participants = group.Participants.Select(p => updatedRecords.TryGetValue(p.Id, out var record) ? GroupGuestOrderStore.Project(record) : p).ToArray(),
                Notifications = [.. group.Notifications ?? [], .. notices], Audit = [.. group.Audit ?? [], .. audit], Version = checked(group.Version + 1) };
            if (await rescheduler.TryRescheduleAsync(group, updated, schedule, ct))
                return new((await ListAsyncForGroup(updated, table!)), group.Id, notices.Select(item => item.Id).ToArray());
        }
        throw new ApiException(409, "Reservation changed. Refresh and try again.");
    }
    private static Task<CreatedBooking> ListAsyncForGroup(GroupAggregate group, BookingTable table)
        => Task.FromResult(new CreatedBooking(group.ReservationId, group.TableId!, group.OwnerSubject,
            DateTimeOffset.Parse(group.Invite.StartTime), DateTimeOffset.Parse(group.Invite.EndTime), group.Invite.MaxGuestCount,
            group.ReservationServiceStatus, group.Invite.LocationAddress, table.Capacity, group.Invite.RestaurantName, "", group.Status == "Submitted"));
    public async Task<CreatedBooking[]> ListForStaffAsync(ILocationBookingReader reader, string subject, CancellationToken ct = default)
    {
        var locations = (config.GetSection("Restaurant:Staff").Get<RestaurantStaffAssignment[]>() ?? [])
            .Where(assignment => !string.IsNullOrWhiteSpace(subject) && assignment.Subject == subject)
            .Select(assignment => assignment.LocationId).Distinct(StringComparer.Ordinal).ToArray();
        if (locations.Length == 0) throw new ApiException(403, "Restaurant staff access is required.");
        var tables = config.GetSection("Restaurant:Tables").Get<BookingTable[]>() ?? [];
        var bookings = new List<CreatedBooking>();
        foreach (var location in locations)
            foreach (var group in await reader.ListLocationAsync(location, ct))
            {
                if (group.Invite.LocationId != location || group.TableId is null) continue;
                bookings.Add(new(group.ReservationId, group.TableId, "", DateTimeOffset.Parse(group.Invite.StartTime),
                    DateTimeOffset.Parse(group.Invite.EndTime), group.Invite.MaxGuestCount, group.ReservationCancelled ? 1 : group.ReservationServiceStatus,
                    group.Invite.LocationAddress, tables.SingleOrDefault(t => t.Id == group.TableId)?.Capacity ?? group.Invite.MaxGuestCount,
                    group.Invite.RestaurantName, "", group.Status == "Submitted", $"Table {group.Invite.TableNumber}", group.Invite.InviterName, "Customer",
                    group.RestaurantReceipt?.Items.Sum(item => item.Quantity)));
            }
        return bookings.OrderByDescending(booking => booking.StartTime).ToArray();
    }
    public async Task<CreatedBooking[]> ListAsync(IOwnerBookingReader reader, string subject, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(subject)) throw new ApiException(401, "Authentication required.");
        var tables = config.GetSection("Restaurant:Tables").Get<BookingTable[]>() ?? [];
        var groups = await reader.ListAsync(subject, ct);
        return groups.OrderByDescending(group => group.Invite.StartTime, StringComparer.Ordinal).Select(group =>
            new CreatedBooking(group.ReservationId, group.TableId!, group.OwnerSubject,
                DateTimeOffset.Parse(group.Invite.StartTime, System.Globalization.CultureInfo.InvariantCulture),
                DateTimeOffset.Parse(group.Invite.EndTime, System.Globalization.CultureInfo.InvariantCulture), group.Invite.MaxGuestCount,
                group.ReservationCancelled ? 1 : group.ReservationServiceStatus, group.Invite.LocationAddress, tables.SingleOrDefault(table => table.Id == group.TableId)?.Capacity ?? group.Invite.MaxGuestCount,
                group.Invite.RestaurantName, "", group.Status == "Submitted")).ToArray();
    }
    public async Task<StaffStatusResult> ChangeStaffStatusAsync(string reservationId, string subject, int newStatus, IGroupRepository groups, CancellationToken ct = default)
    {
        var locations = (config.GetSection("Restaurant:Staff").Get<RestaurantStaffAssignment[]>() ?? [])
            .Where(item => !string.IsNullOrWhiteSpace(subject) && item.Subject == subject)
            .Select(item => item.LocationId).ToHashSet(StringComparer.Ordinal);
        if (locations.Count == 0) throw new ApiException(403, "Restaurant staff access is required.");
        var groupId = await groups.FindGroupByReservationAsync(reservationId, ct) ?? throw new ApiException(404, "Reservation not found.");
        var group = await groups.GetGroupAsync(groupId, ct) ?? throw new ApiException(404, "Reservation not found.");
        if (!locations.Contains(group.Invite.LocationId)) throw new ApiException(403, "This reservation belongs to another location.");
        if (group.ReservationCancelled || group.Status != "Submitted")
            throw new ApiException(409, "Service status is available after a group order is submitted.");
        if (!((group.ReservationServiceStatus == 0 && newStatus == 2) || (group.ReservationServiceStatus == 2 && newStatus == 3)))
            throw new ApiException(409, "This service status change is not allowed.");
        var updated = group with { ReservationServiceStatus = newStatus, Version = checked(group.Version + 1) };
        if (!await groups.SaveAsync(updated, group.Version, null, ct)) throw new ApiException(409, "Reservation changed. Refresh and try again.");
        return new(reservationId, newStatus);
    }
    public async Task<CancelBookingResult> CancelAsync(string id, string subject, IGroupRepository groups, IBookingCancellation cancellation, CancellationToken ct = default)
    {
        var groupId = await groups.FindGroupByReservationAsync(id, ct) ?? throw new ApiException(404, "Reservation not found.");
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var group = await groups.GetGroupAsync(groupId, ct) ?? throw new ApiException(404, "Reservation not found.");
            if (group.OwnerSubject != subject) throw new ApiException(403, "Only the owner may cancel.");
            if (group.ReservationCancelled) return new("Reservation already cancelled.", group.Id, []);
            if (group.Status != "Open") throw new ApiException(409, "Contact the restaurant to cancel a submitted reservation.");
            if (group.TableId is null || string.IsNullOrWhiteSpace(group.OwnerEmail)) throw new ApiException(409, "Reservation requires migration.");
            var start = DateTimeOffset.Parse(group.Invite.StartTime, System.Globalization.CultureInfo.InvariantCulture);
            if (clock.GetUtcNow() >= start) throw new ApiException(409, "Contact the restaurant to cancel this reservation.");
            var schedule = await writer.ReadScheduleAsync(group.TableId, ct);
            var records = (group.GuestOrders?.Values ?? Enumerable.Empty<StoredGuestOrder>())
                .Where(record => group.Participants.Any(person => person.Id == record.Id && person.Type == "Guest")).ToArray();
            if ((group.Notifications?.Length ?? 0) + records.Length > 200 || (group.Audit?.Length ?? 0) + records.Length > 500)
                throw new ApiException(409, "Notification/history capacity reached.");
            var now = clock.GetUtcNow(); var notifications = new List<OrderChangeNotification>(); var audit = new List<OrderAuditEntry>();
            foreach (var record in records)
            {
                var summary = $"Організатор скасував бронювання та замовлення гостя {record.GuestName}";
                notifications.Add(new(Guid.NewGuid().ToString("N"), record.Id, record.Email, group.OwnerEmail, summary, false, now, record.State.Revision, []));
                audit.Add(new(Guid.NewGuid().ToString("N"), subject, record.Id, "CancelReservation", now, summary));
            }
            var updated = group with { ReservationCancelled = true, Status = "Closed", OrderStatus = "Cancelled", Version = checked(group.Version + 1),
                Notifications = [.. group.Notifications ?? [], .. notifications], Audit = [.. group.Audit ?? [], .. audit] };
            if (await cancellation.TryCancelAsync(updated, schedule, ct)) return new("Reservation cancelled.", group.Id, notifications.Select(item => item.Id).ToArray());
        }
        throw new ApiException(409, "Reservation changed. Refresh and try again.");
    }
    public async Task<CreatedBooking> CreateAsync(CreateBookingRequest request, string subject, string? verifiedEmail, string? name, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(subject)) throw new ApiException(401, "Authentication required.");
        if (string.IsNullOrWhiteSpace(verifiedEmail)) throw new ApiException(403, "A verified organizer email is required.");
        var table = (config.GetSection("Restaurant:Tables").Get<BookingTable[]>() ?? []).SingleOrDefault(item => item.Id == request.TableId);
        var location = (config.GetSection("Restaurant:Locations").Get<BookingLocation[]>() ?? []).SingleOrDefault(item => item.Id == table?.LocationId);
        if (table is null || location is null) throw new ApiException(400, "Table is not available.");
        var now = clock.GetUtcNow();
        BookingRules.Validate(request, table, [], now);
        var id = Guid.NewGuid().ToString("N"); var groupId = Guid.NewGuid().ToString("N");
        var ownerId = Guid.NewGuid().ToString("N"); var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var ownerName = string.IsNullOrWhiteSpace(name) ? "Організатор" : name.Trim()[..Math.Min(name.Trim().Length, 100)];
        var invite = new GuestInvite(groupId, location.Id, location.RestaurantName, location.Address,
            request.StartTime.ToString("O"), request.EndTime.ToString("O"), request.GuestCount, table.Number, ownerName);
        var preparation = config.GetValue<int?>("Restaurant:PreparationMinutes") ?? 30;
        var review = config.GetValue<int?>("Restaurant:ReviewMinutes") ?? 15;
        var deadline = OrderDeadlines.FromReservation(request.StartTime, TimeSpan.FromMinutes(preparation), TimeSpan.FromMinutes(review));
        var group = new GroupAggregate(groupId, id, subject, ownerId, "Open", Guid.NewGuid().ToString("N"), "Draft", token,
            invite, [new(ownerId, "Owner", ownerName, false, [])], [], SubmissionDeadline: deadline.SubmissionCloses, OwnerEmail: verifiedEmail, TableId: table.Id);
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var schedule = await writer.ReadScheduleAsync(table.Id, ct);
            BookingRules.Validate(request, table, schedule.Intervals, now);
            if (await writer.TryCreateAsync(group, request, table, schedule, now, ct))
                return new(id, table.Id, subject, request.StartTime, request.EndTime, request.GuestCount, 0, location.Address, table.Capacity, location.RestaurantName, "");
        }
        throw new ApiException(409, "The table changed. Please try again.");
    }
}
