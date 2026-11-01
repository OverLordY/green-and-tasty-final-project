using System.Security.Claims;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using Amazon;
using Amazon.DynamoDBv2;
using Amazon.SimpleEmailV2;
using Amazon.Lambda.AspNetCoreServer.Hosting;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using Microsoft.AspNetCore.Mvc;
using Restaurant.Api;

var builder = WebApplication.CreateBuilder(args);
if (builder.Configuration.GetValue<bool>("Restaurant:UseExampleCatalog"))
{
    if (!builder.Environment.IsDevelopment()) throw new InvalidOperationException("The example catalog is allowed only in Development.");
    builder.Configuration.AddJsonFile("appsettings.Catalog.example.json", optional: false, reloadOnChange: false)
        .AddEnvironmentVariables();
}
// Uses API Gateway HTTP API payload v2 in Lambda; normal Kestrel behavior locally.
builder.Services.AddAWSLambdaHosting(LambdaEventSource.HttpApi);
var authority = builder.Configuration["Authentication:Authority"];
var audience = builder.Configuration["Authentication:Audience"];
if (!Uri.TryCreate(authority, UriKind.Absolute, out var authorityUri) || authorityUri.Scheme != Uri.UriSchemeHttps
    || string.IsNullOrWhiteSpace(audience))
    throw new InvalidOperationException("Set Authentication__Authority to your HTTPS OIDC issuer and Authentication__Audience to the API audience. No demo tokens are accepted.");

var isCognitoIssuer = authorityUri.Host.StartsWith("cognito-idp.", StringComparison.OrdinalIgnoreCase)
    && (authorityUri.Host.EndsWith(".amazonaws.com", StringComparison.OrdinalIgnoreCase)
        || authorityUri.Host.EndsWith(".amazonaws.com.cn", StringComparison.OrdinalIgnoreCase));

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(options =>
{
    options.Authority = authority;
    options.Audience = audience;
    options.RequireHttpsMetadata = true;
    options.MapInboundClaims = false;
    options.Events = new JwtBearerEvents
    {
        OnTokenValidated = context =>
        {
            // The provided Angular client sends its Cognito ID token, whose aud is the app client ID.
            // API Gateway audience validation alone does not distinguish Cognito token types.
            if (isCognitoIssuer && context.Principal?.FindFirst("token_use")?.Value != "id")
                context.Fail("A Cognito ID token is required.");
            return Task.CompletedTask;
        }
    };
    options.TokenValidationParameters = new TokenValidationParameters
    {
        ValidateIssuer = true, ValidateAudience = true, ValidateLifetime = true,
        ValidateIssuerSigningKey = true, RequireSignedTokens = true,
        NameClaimType = "sub", ClockSkew = TimeSpan.FromSeconds(30)
    };
});
builder.Services.AddAuthorization();
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = 429;
    options.AddPolicy("guest-orders", context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 30, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
});
var origins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [];
builder.Services.AddCors(options => options.AddDefaultPolicy(policy => policy
    .WithOrigins(origins).AllowAnyHeader().AllowAnyMethod()));

if (builder.Configuration.GetValue<bool>("Storage:UseInMemory"))
{
    if (!builder.Environment.IsDevelopment()) throw new InvalidOperationException("In-memory storage is allowed only in Development.");
    var ownerSubject = builder.Configuration["Development:OwnerSubject"] ?? "workshop-owner-sub";
    builder.Services.AddSingleton(new InMemoryGroupRepository([DevelopmentFixture.Create(ownerSubject)]));
    builder.Services.AddSingleton<IGroupRepository>(services => services.GetRequiredService<InMemoryGroupRepository>());
    builder.Services.AddSingleton<IBookingWriter>(services => services.GetRequiredService<InMemoryGroupRepository>());
    builder.Services.AddSingleton<IOwnerBookingReader>(services => services.GetRequiredService<InMemoryGroupRepository>());
    builder.Services.AddSingleton<IBookingCancellation>(services => services.GetRequiredService<InMemoryGroupRepository>());
    builder.Services.AddSingleton<IBookingRescheduler>(services => services.GetRequiredService<InMemoryGroupRepository>());
    builder.Services.AddSingleton<ILocationBookingReader>(services => services.GetRequiredService<InMemoryGroupRepository>());
}
else
{
    var tableName = builder.Configuration["DynamoDb:TableName"];
    if (string.IsNullOrWhiteSpace(tableName)) throw new InvalidOperationException("DynamoDb__TableName is required.");
    var region = builder.Configuration["AWS:Region"] ?? "eu-north-1";
    builder.Services.AddSingleton<IAmazonDynamoDB>(_ => new AmazonDynamoDBClient(RegionEndpoint.GetBySystemName(region)));
    builder.Services.AddSingleton<IGroupRepository, DynamoGroupRepository>();
    builder.Services.AddSingleton<DynamoBookingWriter>();
    builder.Services.AddSingleton<IBookingWriter>(services => services.GetRequiredService<DynamoBookingWriter>());
    builder.Services.AddSingleton<IOwnerBookingReader>(services => services.GetRequiredService<DynamoBookingWriter>());
    builder.Services.AddSingleton<IBookingCancellation>(services => services.GetRequiredService<DynamoBookingWriter>());
    builder.Services.AddSingleton<IBookingRescheduler>(services => services.GetRequiredService<DynamoBookingWriter>());
    builder.Services.AddSingleton<ILocationBookingReader>(services => services.GetRequiredService<DynamoBookingWriter>());
}
builder.Services.AddSingleton<GroupService>();
builder.Services.AddSingleton<BookingService>();
builder.Services.AddSingleton<BookingDiscoveryService>();
builder.Services.AddSingleton<IGuestOrderStore, GroupGuestOrderStore>();
builder.Services.AddSingleton(TimeProvider.System);
if (string.Equals(builder.Configuration["Email:Provider"], "SES", StringComparison.OrdinalIgnoreCase))
{
    builder.Services.AddSingleton<IAmazonSimpleEmailServiceV2>(_ =>
        new AmazonSimpleEmailServiceV2Client(RegionEndpoint.GetBySystemName(builder.Configuration["AWS:Region"] ?? "eu-north-1")));
    builder.Services.AddSingleton<IGuestConfirmationMailer, SesGuestConfirmationMailer>();
    builder.Services.AddSingleton<IOrderChangeMailer, SesGuestConfirmationMailer>();
    builder.Services.AddSingleton<IGuestAccessMailer, SesGuestConfirmationMailer>();
}
else
{
    builder.Services.AddSingleton<IGuestConfirmationMailer, SmtpGuestConfirmationMailer>();
    builder.Services.AddSingleton<IOrderChangeMailer, SmtpGuestConfirmationMailer>();
    builder.Services.AddSingleton<IGuestAccessMailer, SmtpGuestConfirmationMailer>();
}
builder.Services.AddSingleton<GuestOrderService>();
builder.Services.AddSingleton<GuestAccessRecoveryService>();
builder.Services.AddSingleton<RestaurantOrderService>();
builder.Services.AddSingleton<CurrentUserService>();
builder.Services.AddSingleton<OwnerGuestOrderService>();
builder.Services.AddSingleton<OwnerNotificationService>();

var app = builder.Build();
app.Use(async (context, next) =>
{
    try { await next(context); }
    catch (ApiException exception)
    {
        context.Response.StatusCode = exception.Status;
        await context.Response.WriteAsJsonAsync(new { error = exception.Message }, context.RequestAborted);
    }
});
app.UseCors();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();
static string Subject(ClaimsPrincipal user)
    => user.FindFirstValue("sub") ?? throw new ApiException(401, "A subject claim is required.");

app.MapGet("/health", () => Results.Ok(new { status = "ok" })).AllowAnonymous();
app.MapGet("/me", (ClaimsPrincipal user, CurrentUserService service, HttpContext context) =>
{
    context.Response.Headers.CacheControl = "no-store";
    return Results.Ok(service.Read(user));
}).RequireAuthorization();
app.MapGet("/locations", (BookingDiscoveryService service) => Results.Ok(service.Locations())).AllowAnonymous();
app.MapGet("/dishes", (string? dishType, string? sortBy, string? name, BookingDiscoveryService service) =>
    Results.Ok(service.Dishes(dishType: dishType, sortBy: sortBy, name: name))).AllowAnonymous();
app.MapGet("/dishes/popular", (BookingDiscoveryService service) => Results.Ok(service.Dishes().Take(4))).AllowAnonymous();
app.MapGet("/locations/{id}/dishes", (string id, BookingDiscoveryService service) =>
    service.Locations().Any(location => location.Id == id) ? Results.Ok(service.Dishes(locationId: id)) : Results.NotFound()).AllowAnonymous();
app.MapGet("/locations/{id}", (string id, BookingDiscoveryService service) =>
    service.Locations().SingleOrDefault(location => location.Id == id) is { } location ? Results.Ok(location) : Results.NotFound()).AllowAnonymous();
app.MapGet("/waiters/{waiterId}/location", (string waiterId, ClaimsPrincipal user, IConfiguration config) =>
{
    if (Subject(user) != waiterId) throw new ApiException(403, "Access denied.");
    var assignments = (config.GetSection("Restaurant:Staff").Get<RestaurantStaffAssignment[]>() ?? []).Where(item => item.Subject == waiterId).ToArray();
    if (assignments.Length != 1) throw new ApiException(409, "Exactly one assigned location is required for this view.");
    return Results.Ok(new { locationId = assignments[0].LocationId });
}).RequireAuthorization();
app.MapGet("/available-tables", async (string? locationId, int capacity, DateTimeOffset? startTime, BookingDiscoveryService service, CancellationToken ct) =>
    Results.Ok(await service.TablesAsync(locationId, capacity, startTime, ct))).AllowAnonymous();
app.MapGet("/tables/{tableId}/available-time-slots", async (string tableId, DateTimeOffset startTime, BookingDiscoveryService service, CancellationToken ct) =>
    Results.Ok(await service.SlotsAsync(tableId, startTime, ct))).AllowAnonymous();
app.MapPost("/reservations", async (CreateBookingRequest request, ClaimsPrincipal user, BookingService service, CancellationToken ct) =>
{
    var email = string.Equals(user.FindFirstValue("email_verified"), "true", StringComparison.OrdinalIgnoreCase) ? user.FindFirstValue("email") : null;
    var booking = await service.CreateAsync(request, Subject(user), email, user.FindFirstValue("name"), ct);
    return Results.Json(booking, statusCode: 201);
}).RequireAuthorization();
app.MapPost("/reservations/edit", async (EditBookingRequest request, ClaimsPrincipal user, BookingService service,
    IGroupRepository groups, IBookingRescheduler rescheduler, CancellationToken ct) =>
    Results.Ok(await service.EditAsync(request, Subject(user), groups, rescheduler, ct))).RequireAuthorization();
app.MapGet("/reservations", async (ClaimsPrincipal user, BookingService service, IOwnerBookingReader reader, HttpContext context, CancellationToken ct) =>
{
    context.Response.Headers.CacheControl = "no-store";
    return Results.Ok(await service.ListAsync(reader, Subject(user), ct));
}).RequireAuthorization();
app.MapGet("/restaurant/reservations", async (ClaimsPrincipal user, BookingService service, ILocationBookingReader reader, HttpContext context, CancellationToken ct) =>
{
    context.Response.Headers.CacheControl = "no-store";
    return Results.Ok(await service.ListForStaffAsync(reader, Subject(user), ct));
}).RequireAuthorization();
app.MapPost("/restaurant/reservations/{reservationId}/status", async (string reservationId, StaffStatusChange request,
    ClaimsPrincipal user, BookingService service, IGroupRepository groups, HttpContext context, CancellationToken ct) =>
{
    context.Response.Headers.CacheControl = "no-store";
    return Results.Ok(await service.ChangeStaffStatusAsync(reservationId, Subject(user), request.NewStatus, groups, ct));
}).RequireAuthorization();
app.MapDelete("/reservations", async ([FromBody] CancelBookingRequest request, ClaimsPrincipal user, BookingService service, IGroupRepository groups, IBookingCancellation cancellation, CancellationToken ct) =>
    Results.Ok(await service.CancelAsync(request.Id, Subject(user), groups, cancellation, ct))).RequireAuthorization();
app.MapGet("/restaurant/reservations/{reservationId}/pre-order", async (string reservationId, ClaimsPrincipal user,
    RestaurantOrderService service, HttpContext context, CancellationToken ct) =>
{
    context.Response.Headers.CacheControl = "no-store";
    return Results.Ok(await service.GetAsync(reservationId, Subject(user), ct));
}).RequireAuthorization();
app.MapGet("/guest/groups/{groupReservationId}/sessions", async (string groupReservationId, ClaimsPrincipal user, GroupService service, CancellationToken cancellationToken)
    => Results.Ok(await service.GetSessionsAsync(groupReservationId, Subject(user), cancellationToken))).RequireAuthorization();
app.MapGet("/reservations/{reservationId}/guest-session-id", async (string reservationId, ClaimsPrincipal user, GroupService service, CancellationToken cancellationToken)
    => Results.Ok(await service.GetOwnerSessionAsync(reservationId, Subject(user), cancellationToken))).RequireAuthorization();
app.MapPost("/group-reservations/{groupReservationId}/submit", async (string groupReservationId, SubmitRequest request, ClaimsPrincipal user, GroupService service, CancellationToken cancellationToken) =>
{
    if (request.ExpectedVersion is null || request.ExpectedVersion < 0)
        throw new ApiException(400, "The reviewed group version is required. Refresh and review the order before submitting.");
    return Results.Ok(await service.SubmitAsync(groupReservationId, request.ReservationId, Subject(user), cancellationToken, request.ExpectedVersion));
}).RequireAuthorization();
app.MapDelete("/guest/groups/{groupReservationId}/sessions/{guestSessionId}", async (string groupReservationId, string guestSessionId,
    ClaimsPrincipal user, GroupService service, OwnerGuestOrderService ownerOrders, IGroupRepository groups, CancellationToken cancellationToken) =>
{
    var group = await groups.GetGroupAsync(groupReservationId, cancellationToken);
    if (group?.GuestOrders?.ContainsKey(guestSessionId) == true)
    {
        var email = string.Equals(user.FindFirstValue("email_verified"), "true", StringComparison.OrdinalIgnoreCase) ? user.FindFirstValue("email") : null;
        return Results.Ok(await ownerOrders.RemoveAsync(groupReservationId, guestSessionId, Subject(user), email, cancellationToken));
    }
    return Results.Ok(await service.RemoveAsync(groupReservationId, guestSessionId, Subject(user), cancellationToken));
}).RequireAuthorization();
app.MapGet("/guest/groups/{groupReservationId}/status", async (string groupReservationId, ClaimsPrincipal user, GroupService service, CancellationToken cancellationToken)
    => Results.Ok(await service.GetStatusAsync(groupReservationId, Subject(user), cancellationToken))).RequireAuthorization();
app.MapGet("/reservations/{reservationId}/invite-token", async (string reservationId, ClaimsPrincipal user, GroupService service, CancellationToken cancellationToken)
    => Results.Ok(new { token = await service.GetInviteTokenAsync(reservationId, Subject(user), cancellationToken) })).RequireAuthorization();
app.MapGet("/guest/invites/{token}", async (string token, GroupService service, CancellationToken cancellationToken)
    => Results.Ok(await service.GetInviteAsync(token, cancellationToken))).AllowAnonymous();
app.MapGuestOrders();
app.MapOwnerOrderChanges();
app.Run();

public partial class Program { }
