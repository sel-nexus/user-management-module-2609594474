# User Management Module

Angular 20.3 / TypeScript frontend, ASP.NET Core 8 modular JSON API, and official MongoDB.Driver persistence. Accounts and safe operation events live in MongoDB, not process memory. The approved development contract implements authentication, administrator account creation/discovery/edit/deactivation, own profile/password, audit review and dashboard summaries.

## Requirements and development

Use .NET SDK 8, Node 24, npm, and MongoDB (tested local Mongo and Compose Mongo 8.0.17). From the repository root:

1. Set backend configuration with ASP.NET environment variables or a private `backend/appsettings.Development.json`; `.env` is **not** loaded by .NET. `backend/.env.example` documents keys. Supply `Mongo__Uri` (without a database path), `Mongo__Database`, `Jwt__Secret` (cryptographically random, at least 64 bytes recommended), `Jwt__Issuer`, `Jwt__Audience`, and `Jwt__ExpiryMinutes=30`. Known dummy signing material is rejected at startup. Use a separate database for development.
2. To create the initial administrator, explicitly set `Bootstrap__Enabled=true`, `Bootstrap__Username`, and a real `Bootstrap__Password` of 12–72 UTF-8 bytes. Bootstrap inserts only when that normalized username is absent; it never promotes or resets an existing account. Disable bootstrap after first provisioning. Store and communicate credentials privately; there is no public demo password.
3. Start API: `dotnet run --project backend/UserManagement.Api.csproj --no-launch-profile` with `ASPNETCORE_URLS=http://127.0.0.1:5080` and appropriate environment/configuration.
4. In `frontend`, run `npm ci --no-bin-links`; copy `.env.example` to the gitignored `.env` or set `API_PROXY_TARGET` to the development API origin. `npm start` runs Angular on port 4200. The browser always requests same-origin `/api`; the proxy host is not bundled into production JavaScript.
5. Open the frontend and sign in with the administrator you provisioned. Create User accounts through the account form. Users see only their own profile/password workflows; administrators also see Users, Audit and Dashboard.

On Linux, a random key can be generated privately with `openssl rand -base64 64`. Never commit your runtime configuration or shell-exported secrets. If `.NET` is installed in the guest home, export `PATH="$HOME/.dotnet:$PATH"` and `DOTNET_ROOT="$HOME/.dotnet"`.

## Containers

Export the same backend settings above, then run `docker compose up --build -d`. Compose requires `Jwt__Secret`; bootstrap is disabled by default. The frontend is published on `${FRONTEND_PORT:-8080}`. Separate tier Dockerfiles build independently; frontend nginx proxies `/api` to the backend service and supports SPA refresh routing. The API binds port 5080, frontend port 80. Compose includes a local persistent Mongo volume and service-name networking. It is a **local deployment**, not production MongoDB provisioning.

Verified: Compose builds/starts both images and Mongo; an in-network frontend request to `/api/health` returns `{"status":"healthy"}` and `/` serves the Angular document. In AWS, ALB may route `/api*` directly to the backend; frontend `/` and backend `/api/health` are the component health paths. MongoDB remains an externally supplied production dependency.

## API baseline

Routes use `/api` without trailing slashes. JSON uses strict snake_case allowlists, string ObjectIds, UTC millisecond timestamps. Swagger is available only in Development at `/swagger`.

| Method/path | Access | Result |
|---|---|---|
| POST /api/login | Public | 200 access_token/token_type/expires_at/safe user |
| GET /api/health | Public | 200 live Mongo readiness, safe 503 on outage |
| POST /api/users | Admin | 201 safe created User |
| GET /api/users | Admin | 200 items/limit/skip/total |
| GET /api/users/{id} | Admin | 200 safe selected account |
| PUT /api/users/{id} | Admin | 200 partial approved-field update |
| DELETE /api/users/{id} | Admin | 204 idempotent soft deactivation |
| GET/PUT /api/profile | Active authenticated | 200 own safe profile/update |
| POST /api/profile/password | Active authenticated | 204 verified password change |
| GET /api/logs | Admin | 200 bounded safe event page |
| GET /api/dashboard | Admin | 200 population/country/seven-day summary |

Create requires username/password; optional address/city/country/education. Username is trimmed/lowercased, 3–64 characters, with a database unique index; address max256 and other text max100. PUT users additionally permits username; profile PUT permits only address/city/country/education. Omitted fields stay unchanged; null clears optional fields. Passwords are 12–72 UTF-8 bytes and bcrypt cost12. Role/id/activity/timestamps/password_hash and sensitive identifiers cannot be mass-assigned.

Users query: `limit` default20, range1–100; `skip` default0; literal case-insensitive `search` on username/city; exact `country`, `education`, `is_active`; `sort` username/city/country/education, `direction` asc/desc; default username ascending with stable id tie-break. Logs accept only limit/skip and sort newest timestamp/id first. Unknown/duplicate queries fail validation. Errors use safe ProblemDetails with status/code/traceId and field errors when available: 400 validation, 401 invalid login/token, 403 forbidden, 404 missing entity, 409 duplicate, 503 unavailable database.

## Security, audit and data evolution

JWTs are signed with configured issuer/audience, expire in 30 minutes and stay in browser memory only. Reload/sign-out clears browser state; no refresh/revocation/recovery system is claimed. Active identity and persisted role are rechecked server-side on protected requests. Admin grants are provisioned only by the explicit bootstrap path; newly created accounts are User. Last-write-wins updates are intentional. Password changes use an atomic expected-hash write to preserve a concurrent winner.

Startup verifies Mongo readiness, creates non-destructive unique username and audit ordering indexes, and optionally bootstraps an Admin. This is an idempotent startup initializer, not a versioned migration framework. Existing duplicate usernames prevent index creation; remediate with reviewed backup/forward-fix rather than destructive automatic rewriting. No TTL/purge is configured.

Audit records contain only id, optional target user_id, action, status and timestamp. Unknown login identities omit user_id. Audit append is synchronous **best-effort** with observable safe failures, not transactional completeness or WORM retention. Reads of selected accounts produce read_user events; log/dashboard review does not produce mutation events. Rejected transport/validation DTOs do not execute a domain operation. Dashboard activity counts recorded events in the inclusive preceding seven-day window, excluding future events; missing/blank country is Unknown.

## Tests and builds

Backend: `dotnet test backend/tests/UserManagement.Tests`. Service tests and HTTP tests use unique real Mongo databases and clean them up; `TEST_MONGO_URI` can configure the test connection. Filter one slice with `--filter FullyQualifiedName~CreateUserApiTests` (analogous class names for the other features). These are database-backed service tests, not mocked repository tests.

Frontend, from `frontend`: `npm test`. Native Jasmine/Karma uses TestBed/HttpTestingController. In the sandbox set `TMPDIR=/tmp` to keep Chromium profile locks outside the shared mount and `CHROME_BIN` to an installed Chromium executable. For Playwright-installed Chromium: `export CHROME_BIN="$(node -e "process.stdout.write(require('playwright').chromium.executablePath())")"`.

Browser: `node node_modules/@playwright/test/cli.js install chromium` once if absent, then `node node_modules/@playwright/test/cli.js test`. The configuration starts both real tiers, creates random isolated test credentials/database, and runs serial desktop/mobile Chromium journeys. Expected negative resource errors are associated with exact request identity/status/window; unexpected console errors, page exceptions and request failures fail tests. Test databases named `umm_e2e_*` are diagnostic isolated data; clean only the databases your run created after retaining evidence, never application data. Preflight: `node e2e/preflight.cjs` captures AX tree via Chromium CDP (the removed Playwright accessibility API is replaced), styles, HTML and screenshots under gitignored `evidence/`.

Build: `dotnet build backend/UserManagement.Api.csproj -c Release`; in frontend `npm run build`. Final verification: 692 backend cases, 183 Angular cases, 3 cross-feature integration cases (included in backend total), and all 24 browser cases eventually passed. Initial browser execution exposed inconsistent Mongo timestamp precision and premature test navigation; source timestamps were corrected and strict comparisons preserved. Angular emits a nonfatal optional lmdb cache warning; this affects build speed, not output. Independent test critic passed round3 at0.98/highfails0, with a retained CORS configuration/preflight branch coverage gap. No measured coverage percentage is claimed.

## UI and release boundaries

The UI uses one restrained teal/neutral token system, locally packaged Geist fonts, native Angular forms/HTTP and semantic controls. HLD's overlapping Axios/PrimeNG/Material/Tailwind requests were resolved to native Angular equivalents to avoid duplicate transports/widgets; no unsupported compatibility claim is made. Responsive tables, labels, loading/empty/error/retry and explicit deactivation confirmation are included. No signup: accounts are administrator-provisioned.

Exports, bulk operations, Aadhaar/PAN input/search/exposure and full recovery remain intentionally disabled under the approved policy. Production AWS rollout, TLS/certificates/key rotation, Mongo hosting/network/least-privilege accounts/backups/RPO/RTO, retention/immutability/privacy approvals, load/SLA validation and dependency/image/penetration scanning remain release prerequisites. This delivery does not claim AWS deployment, compliance, zero vulnerabilities, or an unmeasured performance SLA.
