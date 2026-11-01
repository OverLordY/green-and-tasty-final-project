namespace Restaurant.Api;

public sealed record LocationListing(string Id, string Address, string Description, int TotalCapacity, string AverageOccupancy, string ImageUrl, decimal Rating);
public sealed record AvailableTable(string Id, int TableNumber, string LocationId, int Capacity, BookingInterval[] TimeRanges);
public sealed record AvailableTimeRange(DateTimeOffset Start, DateTimeOffset End);
public sealed record PublicDish(string Id, string Name, string ImageUrl, decimal Price, string State, string Weight, string DishType);

public sealed class BookingDiscoveryService(IBookingWriter writer, IConfiguration config, TimeProvider clock)
{
    public PublicDish[] Dishes(string? locationId = null, string? dishType = null, string? sortBy = null, string? name = null)
    {
        var menu = config.GetSection("Restaurant:Menu").Get<MenuEntry[]>() ?? [];
        var items = menu.Where(item => string.IsNullOrEmpty(locationId) || string.IsNullOrEmpty(item.LocationId) || item.LocationId == locationId)
            .Where(item => string.IsNullOrEmpty(dishType) || string.Equals(item.Category, dishType, StringComparison.OrdinalIgnoreCase))
            .Where(item => string.IsNullOrEmpty(name) || item.Name.Contains(name, StringComparison.OrdinalIgnoreCase))
            .DistinctBy(item => item.Id);
        items = sortBy switch { "PriceAscending" => items.OrderBy(item => item.Price), "PriceDescending" => items.OrderByDescending(item => item.Price), _ => items };
        return items.Select(item => new PublicDish(item.Id, item.Name, item.ImageUrl ?? "", item.Price,
            item.Available ? "Available" : "SoldOut", item.Weight ?? "", item.Category)).ToArray();
    }
    private BookingTable[] Tables() => config.GetSection("Restaurant:Tables").Get<BookingTable[]>() ?? [];
    public LocationListing[] Locations()
    {
        var tables = Tables();
        return (config.GetSection("Restaurant:Locations").Get<BookingLocation[]>() ?? []).Select(location =>
            new LocationListing(location.Id, location.Address, location.RestaurantName,
                tables.Where(table => table.Available && table.LocationId == location.Id).Sum(table => table.Capacity), "", "", 0)).ToArray();
    }
    public async Task<AvailableTimeRange[]> SlotsAsync(string tableId, DateTimeOffset start, CancellationToken ct = default)
    {
        var table = Tables().SingleOrDefault(item => item.Id == tableId && item.Available) ?? throw new ApiException(404, "Table not found.");
        if (start <= clock.GetUtcNow()) return [];
        // The existing UI can edit the duration. Availability is checked again atomically on booking.
        var end = start.AddMinutes(config.GetValue<int?>("Restaurant:DefaultStayMinutes") ?? 90);
        var schedule = await writer.ReadScheduleAsync(table.Id, ct);
        return schedule.Intervals.Any(item => start < item.End && item.Start < end) ? [] : [new(start, end)];
    }
    public async Task<AvailableTable[]> TablesAsync(string? locationId, int capacity, DateTimeOffset? start, CancellationToken ct = default)
    {
        if (capacity < 1) throw new ApiException(400, "Guest count must be positive.");
        var locations = Locations().Select(location => location.Id).ToHashSet(StringComparer.Ordinal);
        var result = new List<AvailableTable>();
        foreach (var table in Tables().Where(t => t.Available && t.Capacity >= capacity && locations.Contains(t.LocationId)
            && (string.IsNullOrEmpty(locationId) || t.LocationId == locationId)))
        {
            if (start is not null && (await SlotsAsync(table.Id, start.Value, ct)).Length == 0) continue;
            result.Add(new(table.Id, table.Number, table.LocationId, table.Capacity, []));
        }
        return result.ToArray();
    }
}
