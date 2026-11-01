namespace Restaurant.Api;

// Guest state and its Owner-visible projection are stored in the same group aggregate.
// The repository's conditional group version makes guest edits and group submission compete
// on the same write, so both cannot succeed from the same stale snapshot.
public sealed class GroupGuestOrderStore(IGroupRepository groups, IConfiguration settings) : IGuestOrderStore
{
    public async Task<StoredGuestOrder?> FindAsync(string id, CancellationToken cancellationToken)
    {
        var groupId = await groups.FindGroupBySessionAsync(id, cancellationToken);
        if (groupId is null) return null;
        var group = await groups.GetGroupAsync(groupId, cancellationToken);
        if (group is null || !group.Participants.Any(person => person.Id == id)) return null;
        return group.GuestOrders?.GetValueOrDefault(id);
    }

    public async Task<bool> CreateAsync(StoredGuestOrder order, CancellationToken cancellationToken)
    {
        if (order.Version != 0) throw new ArgumentOutOfRangeException(nameof(order));
        var group = await groups.GetGroupAsync(order.GroupId, cancellationToken);
        if (group is null || group.Status != "Open" || group.Participants.Length >= group.Invite.MaxGuestCount
            || group.Participants.Any(person => person.Id == order.Id)) return false;
        return await CommitAsync(group, order, true, cancellationToken);
    }

    public async Task<bool> UpdateAsync(StoredGuestOrder order, long expectedVersion, CancellationToken cancellationToken)
    {
        if (expectedVersion < 0 || order.Version != checked(expectedVersion + 1)) throw new ArgumentOutOfRangeException(nameof(order));
        var group = await groups.GetGroupAsync(order.GroupId, cancellationToken);
        var current = group?.GuestOrders?.GetValueOrDefault(order.Id);
        if (group is null || group.Status != "Open" || current?.Version != expectedVersion
            || !group.Participants.Any(person => person.Id == order.Id)) return false;
        return await CommitAsync(group, order, false, cancellationToken);
    }

    private async Task<bool> CommitAsync(GroupAggregate group, StoredGuestOrder order, bool adding, CancellationToken cancellationToken)
    {
        var participant = Project(order);
        var records = new Dictionary<string, StoredGuestOrder>(group.GuestOrders ?? [], StringComparer.Ordinal)
        { [order.Id] = order };
        var people = adding ? group.Participants.Append(participant).ToArray()
            : group.Participants.Select(person => person.Id == order.Id ? participant : person).ToArray();
        var review = settings.GetValue<int?>("Restaurant:ReviewMinutes") ?? 15;
        var submissionDeadline = group.SubmissionDeadline ?? order.State.ChangeDeadline.AddMinutes(review);
        return await groups.SaveAsync(group with
        {
            Participants = people, GuestOrders = records, Version = checked(group.Version + 1),
            SubmissionDeadline = submissionDeadline
        }, group.Version, null, cancellationToken);
    }

    internal static Participant Project(StoredGuestOrder order)
    {
        var state = GuestOrderWorkflow.Restore(order.State);
        static CartItem[] Cart(IEnumerable<GuestOrderLine> lines) => lines.Select(line =>
            new CartItem(new Dish(line.DishId, line.Name, line.UnitPrice), line.Quantity, line.ForWhom, line.Key)).ToArray();
        var confirmed = state.Confirmed.ToArray();
        return new Participant(order.Id, "Guest", order.GuestName, confirmed.Length > 0,
            Cart(confirmed.Length > 0 ? confirmed : state.Pending ?? []),
            state.Pending is null ? null : Cart(state.Pending), state.Status.ToString(), state.Notices.ToArray(), order.OwnerChangeSummary);
    }
}
