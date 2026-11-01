# Backend #5 — Owner group pre-order (.NET 8)

This document includes historical homework implementation notes. For current final-project
capabilities and remaining limitations, see the [project README](../README.md).

This is a **new, isolated implementation** of the four operations in `../homework-backend.md`. The original restaurant Backend is not present in the supplied archive. This project does not recreate its complete login, reservations, menu, guest-cart or deployment ecosystem.

## Implemented

| Method | Route | Access |
| --- | --- | --- |
| GET | `/guest/groups/{groupReservationId}/sessions` | authenticated reservation Owner |
| GET | `/reservations/{reservationId}/guest-session-id` | authenticated reservation Owner |
| POST | `/group-reservations/{groupReservationId}/submit` | authenticated reservation Owner |
| DELETE | `/guest/groups/{groupReservationId}/sessions/{guestSessionId}` | authenticated reservation Owner |
| GET | `/guest/groups/{groupReservationId}/status` | authenticated reservation Owner |
| GET | `/reservations/{reservationId}/invite-token` | authenticated reservation Owner |
| GET | `/guest/invites/{token}` | holder of invitation token |
| GET | `/health` | public |

`POST submit` accepts `{ "reservationId": "...", "expectedVersion": 123 }`, where `expectedVersion` is the version from the group snapshot reviewed by the organizer, and returns an array of `{ "dishId", "quantity" }`. A missing or negative version returns 400, and a stale version returns 409; refresh and review the group before retrying. It consolidates only submitted carts, merges quantities by dish ID, stores the resulting order items, and changes `OrderStatus` to `SubmittedByCustomer` and group `Status` to `Submitted` atomically. Pending carts remain visible in the snapshot but do not enter the order.

Snapshot total includes **all** participant carts; Frontend Grand Total uses only the submitted consolidated order. Monetary calculations use `decimal`.

Contract errors: missing reservation/group/session → 404; foreign-group session → 400; missing order/no submitted carts/no submitted dishes → 400. Authenticated non-owner → 403; missing/invalid credentials → 401. Concurrent stale mutations → 409. Repeated submission of a non-Open group → 400, without duplicating items. As explicit safe implementation decisions, deleting the Owner or deleting after submission/closure also returns 400.

## Build and test (no AWS needed)

Install a .NET 8 SDK, then from `backend/`:

```powershell
dotnet build Restaurant.Api/Restaurant.Api.csproj -c Release
dotnet test Restaurant.Api.Tests/Restaurant.Api.Tests.csproj
```

On this computer the installed SDK can be called explicitly:

```powershell
& 'C:/Users/OverLord/AppData/Local/Microsoft/dotnet/dotnet.exe' test Restaurant.Api.Tests/Restaurant.Api.Tests.csproj
```

Tests run against a detached in-memory repository and ASP.NET TestServer. HTTP tests generate an RSA key in memory and configure a static test issuer; they never contact AWS or a real OIDC provider. A fake DynamoDB client checks transaction shape and conflict handling. They are not a live DynamoDB integration test.

## Authentication: no demo bypass

Configure a real HTTPS OIDC authority and API audience:

```powershell
$env:Authentication__Authority = 'https://YOUR-OIDC-ISSUER'
$env:Authentication__Audience = 'YOUR-API-AUDIENCE'
```

The API validates token signature, issuer, audience and lifetime, and matches the `sub` claim against the stored `OwnerSubject`. It deliberately rejects the unsigned `?demo` frontend token. No login endpoint or token issuer is implemented here. Do not store JWTs, passwords, AWS access keys or signing secrets in the repository.

Missing auth configuration fails startup rather than silently enabling anonymous access. The sample strings above are placeholders; they must be replaced before running the API against a real identity provider.

## Development-only local fixture

To exercise the API host with an existing real OIDC provider but no database:

```powershell
$env:ASPNETCORE_ENVIRONMENT = 'Development'
$env:Storage__UseInMemory = 'true'
$env:Development__OwnerSubject = 'THE-SUB-CLAIM-OF-YOUR-TEST-USER'
dotnet run --project Restaurant.Api/Restaurant.Api.csproj --urls http://localhost:5080
```

Set the authority/audience variables above as well. In-memory mode is forbidden outside Development and still requires a valid signed JWT. Fixture IDs:

- reservation: `workshop-reservation`
- group: `workshop-group`
- Owner session: `workshop-owner-session`
- guest session: `workshop-guest-session`
- pending session: `workshop-pending-session`
- public demo invitation: `workshop-public-invite`

Fixture state resets when the process restarts. It is a local fixture, not the original reservation creation flow. Its default example Owner subject is `workshop-owner-sub`, which grants no access unless a trusted issuer actually authenticates that subject.

Example request using a real token kept only in your shell:

```powershell
Invoke-RestMethod -Uri 'http://localhost:5080/guest/groups/workshop-group/sessions' -Headers @{ Authorization = "Bearer $env:TEST_ACCESS_TOKEN" }
```

For the local fixture the Owner and submitted Guest produce 3 Spring Salads and 1 bowl; the full snapshot additionally includes a pending bowl. API tests calculate and assert both semantics.

## Persistent DynamoDB adapter

DynamoDB is the default storage; it never silently falls back to memory. Configure:

```powershell
$env:Storage__UseInMemory = 'false'
$env:AWS__Region = 'eu-north-1'
$env:DynamoDb__TableName = 'YOUR-GROUP-AGGREGATE-TABLE'
```

Provide credentials through the AWS SDK credential chain. Local profile sessions must be current; cloud runtime should use an execution role with scoped permissions. No table, role or AWS resource is created automatically by this project.

Required table: string partition key `PK`, no sort key. Item types:

| PK | Other attributes |
| --- | --- |
| `GROUP#<groupId>` | `version` Number, `data` String containing serialized `GroupAggregate` |
| `RESERVATION#<reservationId>` | `groupId` String |
| `SESSION#<sessionId>` | `groupId` String |
| `INVITE#<unguessableToken>` | `groupId` String |

All participant carts, order ID, order status, consolidated order items, owner subject and group status live in the **same bounded group aggregate**. This makes order/group state changes atomic. Reservation/session/invite lookup items must be initialized consistently by the reservation/guest-session creation flows, which are outside this four-endpoint feature.

`GroupAggregate` field names and shapes are defined in `Restaurant.Api/Models.cs`; the default serializer writes PascalCase storage JSON. `data.Version` must equal the separate numeric `version` attribute, starting at 0. No ORM or guessed original table mapping is used.

Reads are strongly consistent. Save uses `TransactWriteItems`: conditional replacement of the group (`version == expectedVersion`), a condition that the reservation still references this group, and, for removal, conditional deletion of the session lookup. All changes succeed together or fail. Client-visible version conflicts return 409; retries must refresh data first. The SDK request token makes transport retries of the same transaction safe. Other persistence failures are not misreported as successful writes.

The aggregate is appropriate for a small workshop group. DynamoDB's 400 KB item limit still applies; this is not an unlimited scalable-cart design. All future cart/session writes must use the same versioned aggregate protocol to avoid overwriting concurrent changes. There is no multi-Region design.

Minimum runtime IAM actions are `dynamodb:GetItem`, `dynamodb:PutItem`, `dynamodb:DeleteItem`, and `dynamodb:ConditionCheckItem`, scoped to the configured table (transactional actions are authorized using their underlying item operations). Provisioning/import permissions belong to a separate setup workflow. Confirm against current AWS IAM documentation when deploying.

The supplied `dishes.json` and `locations.json` target `Dishes` and `Locations` tables; they do **not** initialize this aggregate table, reservations, groups, sessions or orders. No seed import was performed.

## Frontend integration boundary

Point `environment.development.ts` at this host only after configuring real authentication and storage/fixtures. Remove `?demo` credentials by signing out and authenticating with the real identity provider. CORS defaults to `http://localhost:4200`; configure explicit production origins when deploying. Configure `AllowedHosts` to the deployed hostname; the checked-in default allows localhost only.

The Owner screen can use the three extra read helpers above. However the **full** level-3 acceptance flow (real sign-up/login, book a table, obtain invite, guest joins/selects/submits, Owner creates their pre-order) still needs the missing original platform or additional implementation. This project does not supply `/signin`, `/register`, `/me`, reservation creation, menu/table discovery, guest session/cart creation or Owner `/orders` CRUD. A four-endpoint implementation plus polling is not a claim of complete restaurant-platform integration.

The API now includes a Lambda HTTP API adapter. Infrastructure configuration and deployment are handled by the separate deployment workflow; this adapter change itself performs no AWS operation.

## Sources used for implementation

- ASP.NET JWT validation: https://learn.microsoft.com/en-us/aspnet/core/security/authentication/configure-jwt-bearer-authentication?view=aspnetcore-8.0
- DynamoDB concurrent writes: https://docs.aws.amazon.com/amazondynamodb/latest/developerguide/BestPractices_ImplementingVersionControl.html
- DynamoDB transactions: https://docs.aws.amazon.com/amazondynamodb/latest/APIReference/API_TransactWriteItems.html
- Package versions pinned in project files; AWS SDK and .NET 8 authentication package checked against NuGet.

## Lambda HTTP API adapter and Cognito token policy

`Amazon.Lambda.AspNetCoreServer.Hosting` is pinned to 2.2.1 (official NuGet package, .NET 8 compatible). `Program.cs` registers `AddAWSLambdaHosting(LambdaEventSource.HttpApi)`: in Lambda it handles API Gateway HTTP API payload v2; outside Lambda the normal local/test host remains available.

Publish from `backend/` for the planned ARM64 managed .NET 8 runtime:

```powershell
dotnet publish Restaurant.Api/Restaurant.Api.csproj -c Release -r linux-arm64 --self-contained false -p:PublishReadyToRun=false
```

Package the contents (not the parent folder) of `Restaurant.Api/bin/Release/net8.0/linux-arm64/publish/`. Managed Lambda runtime: `dotnet8`; architecture: `arm64`; executable-assembly handler: `Restaurant.Api`. No custom bootstrap or self-contained runtime is required for this form.

Deployment must set the real `Authentication__Authority`, `Authentication__Audience`, `DynamoDb__TableName`, `AWS__Region`, `AllowedHosts` (API hostname, or `*` behind API Gateway), and allowed frontend origin(s). Persistent storage remains default, and the execution role must have scoped DynamoDB permissions. API Gateway's JWT authorizer should use the same Cognito issuer and app client audience; application JWT validation remains enabled as defense in depth.

The existing Angular client sends a Cognito **ID token**. For a configured issuer whose host matches Cognito (`cognito-idp.*.amazonaws.com` or `.amazonaws.com.cn`), the API now additionally requires the signed claim `token_use = id`. Tokens with `access`, another value or a missing claim return 401 even if their signature and audience are otherwise valid. Generic non-Cognito OIDC issuers retain normal signature/issuer/audience/lifetime validation without requiring Cognito-specific claims. This policy is deliberate compatibility with the provided frontend, not a claim that ID tokens are universally interchangeable with OAuth access tokens. Offline validation does not detect token revocation before expiry.

Additional HTTP tests cover accepted Cognito ID tokens, four rejected token-use cases, anonymous and foreign-owner DELETE rejection, missing and foreign-group sessions, and successful removal with updated total. The test issuer uses generated RSA keys and static in-process OIDC metadata; no Cognito/AWS request is made by these tests.

Primary references:
- https://www.nuget.org/packages/Amazon.Lambda.AspNetCoreServer.Hosting/2.2.1
- https://github.com/aws/aws-lambda-dotnet/tree/master/Libraries/src/Amazon.Lambda.AspNetCoreServer.Hosting
- https://docs.aws.amazon.com/cognito/latest/developerguide/amazon-cognito-user-pools-using-tokens-verifying-a-jwt.html
