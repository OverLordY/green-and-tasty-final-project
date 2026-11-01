using System.Text.Json;

namespace Restaurant.Api;

// Test/development fixture only. Program explicitly forbids it in Production.
public sealed class InMemoryGroupRepository : IGroupRepository, IBookingWriter, IOwnerBookingReader, IBookingCancellation, ILocationBookingReader, IBookingRescheduler
{
    private readonly object gate = new();
    private readonly Dictionary<string, GroupAggregate> groups = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TableSchedule> schedules = new(StringComparer.Ordinal);
    public Task<bool> TryRescheduleAsync(GroupAggregate previous, GroupAggregate updated, TableSchedule schedule, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        lock (gate)
        {
            if (previous.TableId is null || updated.TableId != previous.TableId || updated.Version != previous.Version + 1
                || !groups.TryGetValue(previous.Id, out var current) || current.Version != previous.Version
                || !schedules.TryGetValue(previous.TableId, out var currentSchedule) || currentSchedule.Version != schedule.Version) return Task.FromResult(false);
            var start = DateTimeOffset.Parse(previous.Invite.StartTime); var end = DateTimeOffset.Parse(previous.Invite.EndTime);
            var others = currentSchedule.Intervals.Where(item => !(item.TableId == previous.TableId && item.Start == start && item.End == end)).ToArray();
            if (others.Length != currentSchedule.Intervals.Length - 1) return Task.FromResult(false);
            var newStart = DateTimeOffset.Parse(updated.Invite.StartTime); var newEnd = DateTimeOffset.Parse(updated.Invite.EndTime);
            if (others.Any(item => newStart < item.End && item.Start < newEnd)) return Task.FromResult(false);
            schedules[previous.TableId] = new(currentSchedule.Version + 1, [.. others, new(previous.TableId, newStart, newEnd)]);
            groups[updated.Id] = Copy(updated);
            return Task.FromResult(true);
        }
    }
    public Task<GroupAggregate[]> ListLocationAsync(string locationId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        lock (gate) return Task.FromResult(groups.Values.Where(group => group.Invite.LocationId == locationId && group.TableId is not null).Select(Copy).ToArray());
    }
    public Task<bool> TryCancelAsync(GroupAggregate group, TableSchedule schedule, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        lock (gate)
        {
            if (group.TableId is null || !groups.TryGetValue(group.Id, out var current) || current.Version != group.Version - 1
                || !schedules.TryGetValue(group.TableId, out var currentSchedule) || currentSchedule.Version != schedule.Version) return Task.FromResult(false);
            var start = DateTimeOffset.Parse(group.Invite.StartTime); var end = DateTimeOffset.Parse(group.Invite.EndTime);
            var intervals = currentSchedule.Intervals.Where(item => !(item.TableId == group.TableId && item.Start == start && item.End == end)).ToArray();
            if (intervals.Length != currentSchedule.Intervals.Length - 1) return Task.FromResult(false);
            schedules[group.TableId] = new(currentSchedule.Version + 1, intervals); groups[group.Id] = Copy(group);
            return Task.FromResult(true);
        }
    }
    public Task<GroupAggregate[]> ListAsync(string subject, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        lock (gate) return Task.FromResult(groups.Values.Where(group => group.OwnerSubject == subject && group.TableId is not null).Select(Copy).ToArray());
    }
    public Task<TableSchedule> ReadScheduleAsync(string tableId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        lock (gate) return Task.FromResult(schedules.GetValueOrDefault(tableId) ?? new TableSchedule(0, []));
    }
    public Task<bool> TryCreateAsync(GroupAggregate group, CreateBookingRequest request, BookingTable table, TableSchedule schedule, DateTimeOffset now, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        lock (gate)
        {
            var current = schedules.GetValueOrDefault(table.Id) ?? new TableSchedule(0, []);
            if (current.Version != schedule.Version || groups.ContainsKey(group.Id) || groups.Values.Any(g => g.ReservationId == group.ReservationId || g.InviteToken == group.InviteToken)) return Task.FromResult(false);
            BookingRules.Validate(request, table, current.Intervals, now);
            schedules[table.Id] = new(current.Version + 1, [.. current.Intervals, new(request.TableId, request.StartTime, request.EndTime)]);
            groups.Add(group.Id, Copy(group));
            return Task.FromResult(true);
        }
    }
    private static GroupAggregate Copy(GroupAggregate group)
        => JsonSerializer.Deserialize<GroupAggregate>(JsonSerializer.Serialize(group))!;

    public InMemoryGroupRepository(IEnumerable<GroupAggregate> initialGroups)
    {
        foreach (var group in initialGroups) groups.Add(group.Id, Copy(group));
    }

    public Task<GroupAggregate?> GetGroupAsync(string groupId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate) return Task.FromResult(groups.TryGetValue(groupId, out var group) ? Copy(group) : null);
    }

    private Task<string?> FindAsync(Func<GroupAggregate, bool> predicate, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate) return Task.FromResult(groups.Values.FirstOrDefault(predicate)?.Id);
    }
    public Task<string?> FindGroupByReservationAsync(string reservationId, CancellationToken cancellationToken)
        => FindAsync(group => group.ReservationId == reservationId, cancellationToken);
    public Task<string?> FindGroupBySessionAsync(string sessionId, CancellationToken cancellationToken)
        => FindAsync(group => group.Participants.Any(person => person.Id == sessionId), cancellationToken);
    public Task<string?> FindGroupByInviteAsync(string token, CancellationToken cancellationToken)
        => FindAsync(group => group.InviteToken == token, cancellationToken);

    public Task<bool> SaveAsync(GroupAggregate group, long expectedVersion, string? removedSessionId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            if (!groups.TryGetValue(group.Id, out var current) || current.Version != expectedVersion)
                return Task.FromResult(false);
            if (removedSessionId is not null && !current.Participants.Any(person => person.Id == removedSessionId))
                return Task.FromResult(false);
            groups[group.Id] = Copy(group);
            return Task.FromResult(true);
        }
    }
}

public static class DevelopmentFixture
{
    // OwnerSubject is an example OIDC sub, never a credential or authentication bypass.
    public static GroupAggregate Create(string ownerSubject = "workshop-owner-sub")
    {
        var salad = new Dish("dish-salad", "Spring Salad", 12m);
        var bowl = new Dish("dish-bowl", "Avocado Pine Nut Bowl", 14.50m);
        return new GroupAggregate(
            "workshop-group", "workshop-reservation", ownerSubject, "workshop-owner-session",
            "Open", "workshop-order", "Draft", "workshop-public-invite",
            new GuestInvite("workshop-group", "workshop-location", "Green & Tasty", "Rustaveli Avenue 48",
                "2026-10-28T16:00:00Z", "2026-10-28T17:30:00Z", 5, 7, "Owner"),
            [
                new("workshop-owner-session", "Owner", "Owner", true, [new(salad, 1), new(bowl, 1)]),
                new("workshop-guest-session", "Guest", "Guest", true, [new(salad, 2)]),
                new("workshop-pending-session", "Guest", "Still choosing", false, [new(bowl, 1)])
            ], []);
    }
}
