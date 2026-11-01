using System.Security.Claims;

namespace Restaurant.Api;

public sealed record CurrentUser(string Id, string FirstName, string LastName, string Email, string Role);
public sealed class CurrentUserService(IConfiguration configuration)
{
    public CurrentUser Read(ClaimsPrincipal principal)
    {
        var subject = principal.FindFirst("sub")?.Value;
        if (string.IsNullOrWhiteSpace(subject)) throw new ApiException(401, "Authentication is required.");
        var staff = configuration.GetSection("Restaurant:Staff").Get<RestaurantStaffAssignment[]>() ?? [];
        var role = staff.Any(item => item.Subject == subject) ? "Waiter" : "Customer";
        return new(subject, principal.FindFirst("given_name")?.Value ?? "",
            principal.FindFirst("family_name")?.Value ?? "", principal.FindFirst("email")?.Value ?? "", role);
    }
}
