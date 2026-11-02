# Green & Tasty — навчальний фінальний проєкт

Групове попереднє замовлення в ресторані з підтвердженням страв гостей
електронною поштою без реєстрації. Організатор створює бронювання, запрошує
гостей, бачить їхні страви та статуси, редагує замовлення й надсилає один
спільний перелік. Офіціант бачить зафіксоване замовлення своєї локації.

**Демонстраційний сайт:** https://d1lv4i6uzy4vra.cloudfront.net/  
**Опис функцій та обмежень:** [FINAL_DEMO_UK.md](FINAL_DEMO_UK.md)

Це навчальна демонстрація, а не сервіс справжнього ресторану. Меню й столики
тестові. Amazon SES перебуває в sandbox: листи надсилаються лише на перевірені
адреси. Доступ до AWS заплановано тільки на період перевірки проєкту.

### Технології та перевірка

Angular, .NET 8, AWS Lambda, DynamoDB, Cognito, SES, S3 та CloudFront.
Серверні тести: `dotnet test backend/Restaurant.Api.Tests/Restaurant.Api.Tests.csproj -c Release`.
Локальний frontend: `cd frontend/restaurant-frontend-app`, `npm ci`, `npm start`.
Для повної роботи потрібна конфігурація API, автентифікації та пошти; приклади
налаштувань і опис розгортання наведено нижче. Додаткові перевірки для ДЗ №8
доступні в [окремому репозиторії ДЗ №8](https://github.com/OverLordY/green-and-tasty-homework8).
Наскрізний сценарій двох підтверджених гостей, організатора й офіціанта
[пройшов у GitHub Actions](https://github.com/OverLordY/green-and-tasty-homework8/actions/runs/41427771285).

Нижче збережено історичні технічні нотатки: твердження про незавершені тести
можуть відображати попередній стан. Актуальне покриття — у репозиторії ДЗ №8.

## Development notes

This is the working full-stack application for the final project and its guest email confirmation feature.
The final product's requirements take priority over preserving individual homework contracts.
The working copy keeps development changes separate while integration is underway.

## Implemented and tested locally

- A domain workflow keeps confirmed and proposed quantities separate.
- Decreases and removal are immediate; an increase needs fresh confirmation.
- A confirmation token is random, stored as a hash, one-time, bound to the current revision,
  and expires no later than the change deadline.
- An independent guest access capability is issued on initial confirmation; cross-order
  capability checks are covered by tests.
- Restaurant preparation time and the review window determine both deadlines.
- Guest order state can be stored and restored without saving raw tokens; conditional writes
  detect conflicting concurrent updates. DynamoDB request construction is tested with a fake.
- Application service checks per-order guest access, validates email format, prepares mail for the
  precise pending version, invalidates the initial draft token after confirmation, and limits
  repeated email requests to one per order every 30 seconds. It reports delivery failure to callers.
- HTTP endpoints now connect the workflow to draft creation by invitation, viewing, dish selection,
  reduction, requesting email and explicit POST confirmation. GET confirmation is read-only.
- Prices come from the server configuration catalog; clients provide dish ID, recipient and quantity.
- Guest route responses use no-store caching and requests have an IP-based limiter.
- An SMTP adapter formats a confirmation email and reports unconfigured/failed delivery as HTTP 503.
- Guest records and the Owner-visible participants are now persisted inside the same group aggregate.
  Confirmed items remain effective while increases await confirmation; recipients and line notices
  are included in the Owner view. Guest state and session lookup share the DynamoDB transaction.
- Guest writes compete with group submission on the same group version. A deterministic concurrency
  test checks that a winning submission rejects the stale guest write without partial persistence.
- Owner removal revokes guest access and excludes the guest from totals; late submission is rejected.
- Backend verification now includes domain, store, service, HTTP, Owner changes and SES adapter tests.
- SES transport is available via Email:Provider=SES. It uses AWS role credentials rather than
  SMTP passwords. The sender identity has been verified in eu-north-1; the SES project remains
  in sandbox, so recipients must also be verified until production access is approved.
  The CloudFormation template accepts an optional `AdditionalSandboxRecipient` for a further
  verified test identity while retaining the configured sender and existing sandbox recipient.
  On 2026-11-01 SES reported production access disabled, sending enabled, BOUNCE/COMPLAINT
  suppression reasons, and feedback forwarding enabled on the verified Gmail sender. No
  project-owned sending domain is configured. The account owner has not submitted the
  [production access request draft](backend/infra/ses-production-request-draft.md); AWS approval,
  abuse controls and operating bounce/complaint handling still require review. Three verified
  addresses remain available for the demo. Identity ARNs in the SES role grant use of sending
  identities; they are not an explicit three-recipient allowlist.
- The Angular frontend is now included in frontend/restaurant-frontend-app. The new
  /guest-confirmation page reads the mail token from the URL fragment, waits for an explicit
  button click, sends POST confirmation and loads the confirmed items with the fresh guest
  capability. It removes the consumed token from the address and uses per-tab storage.
- The Owner card displays pending changes separately, identifies recipients and retains cancelled
  dish names with an explicit cancellation notice. App/spec type checks, selected frontend tests
  (including the new confirmation-card tests) and production build passed.
- /join/:token now opens the new guest order screen. It loads the invitation/menu from the API,
  supports categories, unavailable dishes and ordering for different recipients, collects contact
  information at email submission, and synchronizes absolute quantities before requesting a letter.
- The personal confirmation view supports immediate decreases/removal and pending increases with a
  fresh email request. A confirmed order can be reopened in the same browser tab using its stored
  capability and order ID fragment, without reusing the one-time confirmation token.
- Guests can now recover access from an old email link or the shared invitation page. Personal
  recovery links are one-time, stored as hashes and expire after 15 minutes. A new link can be
  requested after one minute. Public responses do not disclose whether the email owns an order.
- Recovery requires an explicit POST/button and restores access without confirming dishes.
  Redemption revokes previous personal/draft access and previous dish-confirmation challenges.
  After the edit deadline or group submission it provides read-only access; it cannot reopen edits.
- The personal page now loads an authenticated menu, filters categories and adds new dishes for
  named recipients. Additional portions use the current proposed quantity as their starting point.
  Unavailable dishes cannot be added; confirmed totals remain unchanged until explicit confirmation.
- Owner editing now supports adding dishes, changing absolute quantities, immediate reductions,
  clearing one guest's dishes, removing a guest, reminders and clearing the whole group including
  the organizer's dishes. Clearing dishes preserves the participant and personal access; removing
  a guest revokes access. The edit dialog shows recipient, deadline and pending confirmation.
- Owner mutations require the matching authenticated subject plus a server-stored organizer email
  or an email claim explicitly marked email_verified=true. Request bodies cannot set that address.
- Changes and their audit records/mail intents share one conditional aggregate write. Clear-all
  builds every change before saving once, so a deadline failure cannot partially clear the group.
- Reminders apply only to pending dishes before the change deadline, at least one minute apart.
  The guest receives a fresh explicit-confirmation link; the organizer receives only a summary.
- SMTP and SES support separate change notices to the guest and organizer. A persistent two-minute
  lease serializes sending each notification; acceptance is recorded separately per recipient.
  The UI attempts sending after saving and offers explicit retry in History and notifications.
  Old superseded intents never issue links for a newer order version. Notification state updates
  cannot overwrite concurrent dish edits. Raw confirmation/access tokens are never stored in intents.
  When the server reports a recipient remains pending, the open client makes at most three attempts;
  manual retry also remains available in history. A deployed DynamoDB Streams worker processes new
  owner-change, guest confirmation and guest recovery intents independently of the browser. It reuses
  per-recipient delivery/leases and retries unsuccessful records (up to 20 retries or one hour). Guest
  tokens are generated during delivery, and only hashes are stored. Discarded event payloads may contain
  personal data and are kept in a private, encrypted S3 bucket for seven days. A CloudWatch alarm sends
  an operational warning to the configured sender via SES; if SES itself is unavailable that warning can
  also fail, so CloudWatch and the failure bucket remain the independent records. Old owner-change intents
  are not automatically replayed and can be retried from history. On 2026-10-31 a disposable deployed
  group received initial and recovery emails through the worker; the user confirmed both links worked.
  The temporary test records were removed. Failure/retry scenarios are verified locally, not by inducing a
  real SES outage in AWS. A synthetic alarm invocation was accepted by SES and its event rule matched.
- The audit panel shows who changed what and when; pending mail identifies the affected guest/change.
  Mail acceptance means acceptance by the provider, not proven delivery to the recipient's inbox.
- POST /reservations now creates the owner group and invitation together with a table schedule.
  The server reads table/location details from Restaurant:Tables and Restaurant:Locations, requires
  an authenticated owner with a verified email, validates capacity/time/overlaps, and retries
  concurrent version conflicts. In DynamoDB the group, reservation, session, invite and schedule
  are written as one transaction. This is tested with in-memory concurrency and a fake DynamoDB client.
- GET /reservations returns only the authenticated owner's new bookings. An owner index is updated
  in the same transaction as booking creation; the reader verifies ownership again for each group.
  The reservation card links to the owner's group order. The current single-item owner index has a
  bounded DynamoDB item size and must be redesigned or archived for large booking histories.

Verification on 2026-10-30: 137 backend tests and 118 previously selected frontend tests passed. Angular
app/spec TypeScript checks and the production build passed. Existing bundle/style/CommonJS
warnings remain; this is local component/API verification, not a deployed end-to-end test.

## Deployment and remaining integration work

On 2026-10-30 the final project was deployed to AWS in eu-north-1:
https://d1lv4i6uzy4vra.cloudfront.net/. The private S3 frontend is served by CloudFront;
the new API uses Lambda, DynamoDB, Cognito and the verified SES sender. The API health endpoint,
public catalog and denial of unauthenticated protected requests were checked against AWS, and
the home page and Cognito sign-in page were opened in a browser. HTTP tests use an in-memory
store and a fake mail transport; they do not replace a deployed end-to-end test.
An owner test account successfully created a reservation; a guest confirmed a dish by email,
and the owner saw it in the consolidated total. An owner-proposed increase stayed pending
until the guest confirmed it. Reusing the confirmation link was rejected. A later SES test
for another verified address returned AccessDeniedException. The Lambda role was updated to
allow the two configured test identities with a condition restricting the From address; SES
then accepted the second guest's application confirmation email. The recipient confirmed the
message arrived and the order; the deployed group projection showed one confirmed Green Salad.
An additional sandbox recipient was verified and added to the scoped SES role after an initial
application email returned 503. An organizer reminder was accepted by SES; the recipient reported
the email arrived and confirmed the test order. The deployed Owner view then showed the confirmed
Green Salad and $9.99 total. This verifies that reminder and confirmation flow, not the initial
guest email request after the policy update.
A subsequent owner change from one to two portions remained pending until the guest confirmed it;
the user reported both guest and owner received notifications, and the deployed state showed two
confirmed portions afterward. In a separate group, two guests using different email addresses
confirmed their own orders; the owner UI displayed $32.49 and submitted both dishes. The stored
restaurant receipt contained Green Salad and Truffle Mushroom Risotto with the correct totals.
Cross-guest authorization for confirmed personal capabilities remains to be verified end to end.
An authenticated waiter assigned to the same location opened the submitted receipt in the deployed
UI and saw both items and $32.49. Reservation cards now show quantities from the submitted receipt
and do not offer a receipt link when no group order has been submitted. The notification bell and
polling are disabled until waiter notification endpoints are implemented; the interface does not
claim that there are no notifications. For a submitted group order, assigned restaurant staff can
change the reservation from Reserved to In Progress to Finished; this was tested on a disposable
deployed booking, including persistence after reload. Other legacy waiter actions remain unavailable:
creation, cancellation, postponement and table changes are hidden. The list refreshes every 30 seconds
and supports a manual refresh, with an explicit warning if the last successful data is stale.
The HTTP integration suite also verifies
isolation between two confirmed guest credentials, including rejected cross-order read and edits;
this is an in-memory test and is not a live cross-credential test against AWS.
The authenticated organizer can add, reduce or cancel their own dish lines before the restaurant's
change deadline. The server uses its menu for availability and prices and only the group owner can
modify these lines. The organizer's signed-in order is immediately eligible for group submission;
email confirmation of guest orders remains separate. Owner-order HTTP and component tests pass;
the deployed Owner editor was checked with a test dish, then the dish was removed and the total
returned to zero. In two deployed tabs, a quantity change closed the earlier confirmation dialog
and prompted the Owner to review the updated order; the test dish was then removed. Server-side
409 rejection is also covered by an in-memory HTTP test, but was not triggered directly against AWS.
The reservation dashboard reads its status directly from the bookings API, without invoking legacy
orders and feedback endpoints that are absent from this backend. A focused component test and a
deployed browser check verified that reservations load without the previous Not Found notification.
The final-project dashboard supports editing an open reservation before the order-change deadline:
the same table's start time, duration and guest count can be changed. The API checks ownership,
capacity, participant count and table conflicts, and updates the group and schedule atomically.
Confirmed dishes stay confirmed; changing the time updates deadlines and invalidates pending
confirmation links, with fresh confirmation notices prepared for guests with pending dishes.
The owner UI attempts notification delivery and offers retry through group history if needed.
The deployed test booking was changed from two to three guests and then moved from 18:00 to 18:30;
the confirmed salad and $9.99 total remained intact and the table's new slot became unavailable.
The user confirmed both recipients got the guest-count notice and the time-change notices were
understandable. UI time choices currently cover the same day only.
On the deployed site, an owner then proposed a second salad and moved the booking again before
confirmation. The user verified the earlier confirmation link no longer worked and confirmed
the replacement link; the Owner view displayed two confirmed salads and a $19.98 total.
Pre-order opens the implemented group-order view instead of the legacy cart workflow.
The Owner UI sends the reviewed group version when submitting. If the version has changed, the API
returns 409 and the Owner must refresh and review again. The API rejects a missing or negative
`expectedVersion` with 400; older clients must update to send the version they reviewed.
The confirmation-preview API and UI are deployed: local tests verify token validation and
showing the current and proposed items, and an invalid link was checked in the browser;
the updated preview still needs a live test with a fresh, valid email link.
The public catalog and guest-order routes are explicitly allowed while the default route remains
JWT-protected. The supplied catalog and table data are examples, not real restaurant availability.
The first guest confirmation and booking were exercised with live SES and deployed DynamoDB.
Cross-guest authorization for confirmed personal capabilities still requires deployed end-to-end verification.
Two isolated draft orders were created against the deployed API: each draft could read itself,
while both cross-order reads and a cross-order edit returned 401. This does not replace the
remaining check with confirmed personal access capabilities. A separate Sign Up action now opens
the Cognito hosted registration page with its own state and PKCE challenge, including when another
user has an active session. Guest ordering remains registration-free.
Personal access
recovery and new-dish selection are implemented and tested locally, including HTTP and UI components.
Restaurant settings UI still needs integration. A recovery request always returns a generic acknowledgement,
including for an unknown address; the user can request another email after one minute. This maintains
email privacy without falsely claiming delivery. Sending is at-least-once: a crash after provider
acceptance and before recording progress can cause a duplicate email on retry. The current bounded group aggregate supports up to 500 audit records and 200
notification intents; archival/separate storage will be needed for long-lived or high-volume groups.
Legacy participants without guest-workflow records retain the old removal route and cannot use the
new dish editor; production migration/seeding must create records consistent with the final workflow.
The restaurant submitted-order view is implemented locally at
`/restaurant/reservations/:reservationId/pre-order`. It reads a snapshot captured atomically during
Owner submission, including server-stored dish names, quantities and line totals. Only confirmed
items enter that snapshot; pending changes are excluded. Credential recovery and later participant
projections cannot change its totals. Existing submitted orders without this snapshot require migration.
Server configuration `Restaurant:Staff` assigns authenticated subjects to location IDs (entries
with `Subject` and `LocationId`). Without an assignment access is denied, including to the Owner.
No email addresses, personal capabilities or guest records are returned to the restaurant screen.
The legacy waiter screen is separate; the new screen currently supports read-only order viewing,
not restaurant acceptance/rejection actions. Waiter reservation cards link to the new view; the
The frontend now has a Cognito authorization-code + PKCE callback. It exchanges the one-time code,
checks state and a ten-minute request lifetime, validates the ID token through `/me` before saving
the session, and removes the callback query from browser history. Configure COGNITO_DOMAIN and
COGNITO_CLIENT_ID at build time to show the Cognito login button; when absent the legacy form remains.
The existing workshop Cognito pool has admin-created users and no configured managed-login domain;
it has not been changed. A separate managed-login domain and client callback are deployed, but a
complete real-user login and booking verification is still required. Refresh tokens are not stored or refreshed, so users must sign in again when
the current short-lived tokens expire. The existing app stores ID/access tokens in localStorage.
The waiter dashboard now reads a location-scoped staff reservation list. The server uses its own
staff assignments, checks each indexed group against the assigned location, and omits the owner's
subject and email. New bookings add the location index in the same DynamoDB transaction; existing
records need migration, and this single-item index requires archival/redesign at scale. The staff
location selector currently requires exactly one assigned location. Other legacy waiter actions,
including editing reservations, are not yet integrated with the new booking workflow. Booking discovery
now serves configured locations and table availability for the selected start time, with a provisional
90-minute default stay. It currently returns only that selected time, not a full schedule or restaurant
opening hours. Availability is advisory and rechecked atomically at booking. Configure actual server-side tables/locations before using the new
creation endpoint. Owners can cancel an open future reservation: the group closes and the table
schedule is released in one conditional transaction, and guest/owner notification intents are
saved with it. The frontend attempts delivery and reports pending notifications for manual retry.
Submitted reservations and those that have started require contacting the restaurant. Schedule
pruning is not implemented; the current per-table schedule grows over time and needs lifecycle handling.

## HTTP routes

- GET /me — authenticated identity and server-assigned Customer/Waiter role; no-store response.
- POST /reservations — authenticated owner with verified email; server-configured table and location.
- GET /reservations — authenticated owner's bookings, excluding legacy records without an owner index.
- GET /restaurant/reservations — authenticated staff's location-scoped bookings; no owner email or subject.
- DELETE /reservations — authenticated owner cancellation with body `{ "id": "..." }`; atomically
  closes the group/releases the table and returns notification IDs for the client to send.
- POST /guest/orders/ — invitation token, guest name and email; returns the new draft capability.
- GET /guest/orders/{id} — requires the draft/personal bearer capability.
- GET /restaurant/reservations/{reservationId}/pre-order — authenticated, location-assigned staff only;
  returns the fixed submitted order, never draft guest carts.
- POST /guest/orders/{id}/dishes — dish ID, recipient and absolute quantity.
- POST /guest/orders/{id}/reduce — line key and lower quantity (zero removes the dish).
- POST /guest/orders/{id}/request-confirmation — sends the current pending version.
- GET /guest/orders/{id}/confirm — read-only, never confirms.
- POST /guest/orders/{id}/confirm — one-time email token; returns fresh personal capability.
- POST /guest/orders/{id}/request-access — email; generic acknowledgement without disclosing a match.
- POST /guest/orders/recover-access — invitation token and email; sends matching active orders in one letter.
- GET /guest/orders/{id}/recover-access — read-only, never redeems a recovery token.
- POST /guest/orders/{id}/recover-access — one-time recovery token; fresh capability without confirming dishes.
- GET /guest/orders/{id}/menu — requires the matching personal/draft capability; server catalog and edit availability.
- GET /owner/groups/{groupId}/menu — authenticated Owner menu and system cutoff.
- POST /owner/groups/{groupId}/own-order/dishes — authenticated Owner changes their own dish line.
- POST /restaurant/reservations/{reservationId}/status — assigned staff move a submitted booking from reserved to in progress, then finished.
- POST /owner/groups/{groupId}/orders/{id}/dishes — Owner changes a dish/recipient quantity.
- POST /owner/groups/{groupId}/orders/{id}/clear — immediately clears dishes, preserves guest.
- POST /owner/groups/{groupId}/orders/{id}/remind — queues a confirmation reminder.
- POST /owner/groups/{groupId}/orders/clear — atomically clears every participant's dishes.
- GET /owner/groups/{groupId}/history — Owner-only audit history.
- GET /owner/groups/{groupId}/notifications — provider acceptance status, without email addresses/tokens.
- POST /owner/groups/{groupId}/notifications/{id}/send — attempts sending/retries unsent recipients.
- DELETE /guest/groups/{groupId}/sessions/{id} — new guest orders also queue notices and audit removal;
  response contains an optional notificationId for the client's sending attempt.

Restaurant:Menu is the server-owned catalog (Id, Name, Price, Available).
Menu entries may include LocationId; entries with a location are only offered and accepted for that
location. Entries without one remain shared for compatibility with workshop data.
`backend/Restaurant.Api/appsettings.Catalog.example.json` is an optional example derived from the
provided locations.json and dishes.json. The example uses invented table numbers/capacities, a single
generic menu category and assumes all eight dishes available; those fields are demo assumptions, not
verified restaurant information. A copy is deployed in the demonstration Lambda as
`appsettings.Production.json`. To opt in locally, set `ASPNETCORE_ENVIRONMENT=Development` and `Restaurant__UseExampleCatalog=true`; the
application refuses to load the example through this switch outside Development. Environment
variables override the example. An integration test covers discovery, booking, guest confirmation,
submission and the fixed restaurant receipt using this example and an in-memory store.
Use `backend/infra/package-final-api.ps1` with an explicit catalog path, a new publish directory,
and a new ZIP path when packaging Lambda. The script validates nonempty catalog sections and the
presence of the catalog and API assembly in the ZIP. The example catalog is for demonstrations only.
Restaurant:PreparationMinutes defaults to 30 and Restaurant:ReviewMinutes to 15.
These are provisional server settings. Configure actual restaurant timings before real use.
Email configuration uses Email:SmtpHost, Email:SmtpPort (default 587 with TLS), Email:Sender,
Email:FrontendBaseUrl (HTTPS), and optionally Email:SmtpUsername/Email:SmtpPassword.
Supply credentials through protected environment configuration, never commit them.
For SES set Email:Provider=SES, Email:Sender and Email:FrontendBaseUrl. The Lambda execution roles
scope their sending identities and require the configured From address. SES sandbox limits recipients
to verified email addresses or domains and the mailbox simulator. The IAM identity ARN list is not an
explicit list of permitted recipient addresses. The deployed two-guest confirmation and Owner-to-waiter
flow has been exercised as described below.

## Guest recovery journey

1. Open an earlier order email or `/join/:token` and expand the email access-recovery form.
2. Enter the email used for the order. The screen gives the same conditional acknowledgement
   for all valid email addresses. For invitation recovery, only active orders belonging to that
   email in that group receive links; other guests' names or addresses never appear in the response.
3. Open the new email and press **Restore access** on the page. Merely opening the link has no effect.
   Tokens appear only in the URL fragment and are removed from the visible address after success.
4. Review confirmed and proposed dishes separately. Before the system deadline, add dishes from
   the menu or decrease/cancel immediately, then request fresh confirmation for pending additions.
   Recovering access invalidates previous dish-confirmation links but keeps pending dishes intact.
5. After the deadline or submission, use recovery to view the order. Editing remains closed.

Each link restores one order only. When several orders in the same group use the same email,
the recovery email provides a separate link for each. Removing a participant invalidates outstanding
recovery links. A concurrent redemption can succeed only once; conflicting credential writes cannot
overwrite dishes or revert a group's submitted status. Current browser credentials use per-tab
session storage; opening another device/tab is supported through a new recovery email.

Test:

```powershell
dotnet test backend/Restaurant.Api.Tests/Restaurant.Api.Tests.csproj -c Release
```

## Test Automation homework 8 considerations

The provided homework-aqa.md maps this project to Direction 1, Level 3 after deployment.
The final test repository should cover UI group creation/invitation, guest selection, Owner review,
confirmation and guest removal, plus API authorization/validation checks. The waiter must see the
same submitted order. A manual deployed two-guest-to-waiter journey is recorded below. Seven deployed
Playwright smoke tests have passed locally and in GitHub Actions, along with the backend tests; full
authenticated browser E2E tests remain outstanding. Report Portal is optional.
Tests will use isolated dynamic data, accessible role/label locators, external base URLs/credentials,
and failure traces. API-based setup must not replace the required main user-interface journey.

The complete inherited Angular unit-test suite is not currently green: on 2026-10-31, 360 passed
and 22 failed. Test setup now handles the browser's FileReader getters and supplies routing where
required. Fifteen remaining profile tests concern expectations including photo type, size and
dimension validation and save behavior that do not match the current component. Seven legacy
component smoke tests still need suitable input or authentication fixtures. These results are not
evidence that all UI scenarios work, and require further investigation before release.

## Deployed end-to-end check, 2026-11-01

A disposable booking for November 5 was created from the booking UI with two guests. Each guest
selected a different dish in an independent guest tab and confirmed the correct email. The Owner UI
showed both confirmed carts and $32.49. Removing the second guest removed that cart and reduced the
total to $9.99. The first submit attempt returned a conflict and required a manual refresh; after
refresh the Owner submitted successfully. The persisted restaurant receipt contains only one Green
Salad totaling $9.99. Notification progress also changes the aggregate version, a plausible cause
of this spurious conflict. The Owner page now refreshes the snapshot before presenting the submission
dialog and preserves the server version check; 40 group-page tests pass and the update is deployed.
This reduces the stale-version window but simultaneous later writes can still require review. A second
deployed test used a new November 6 booking, one pending guest, and an Owner dish: after removing the
guest, submission succeeded without a manual refresh. The signed-in waiter
opened the same booking and saw exactly Green Salad × 1 and $9.99; the removed dish was absent.
Separately, an isolated group was seeded from a confirmed test-order state with two fresh test access
capabilities: on the deployed API both own-order reads returned 200, and cross-order reads and edits
returned 401 in both directions. Temporary records were deleted. These credentials were seeded for the
test, not obtained by the two guests through the live confirmation emails.

Homework 8 has a [separate Playwright repository](https://github.com/OverLordY/green-and-tasty-homework8).
Its confirmed-guest Level 3 journey passed on the deployed application; the
repository documents which additional requirements are covered in memory only.
An isolated deployed deadline fixture was also used to verify that the guest preview returns
`canChange=false` and the creation request returns 409 after the edit cutoff; its records were removed.
An 18-test local selection covering deadline, expiry, repeated submission and concurrent updates passed.
