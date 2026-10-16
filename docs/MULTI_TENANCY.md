# Meet to Manage: database per tenant

## Storage split

Each client retains the existing MeetToManage schema: users, profiles, roles,
permissions, menus and all business data. The shared `MasterDbContext` is the
tenant/login context. It stores tenant IDs/codes, generated database names,
lifecycle status, globally unique normalized email/mobile aliases, one-tenant
user memberships, and BCrypt PIN hash mirrors. It contains no user profiles or
business records. Passwords/PINs are never stored as plaintext.

The tenant user remains authoritative for PIN verification, lockout, status and
permissions. A stale central hash cannot authorize an old PIN. Database mappings
store the database name against tenant ID; common server credentials remain in
server configuration and never appear in tenant responses or tokens.

## Login and request flow

1. POST `/api/auth/login` with `{ email, pin }`; mobile can be entered in `email`.
2. Normalize the identifier, find its tenant in master, and require Active status.
3. Bind that database before MVC constructs AuthService and UnitOfWork.
4. Verify the current tenant user's PIN, lockout and account status.
5. Issue a JWT containing `tenant_id`, `tenant_code`, user and permission claims.
6. Validate subsequent JWTs, resolve their ID/code together in master, then create
   the scoped business context using the registered database.

A scope cannot switch tenants. There is no fallback client database. Current
account access checks fail closed in tenant mode instead of accepting stale
permission claims on database errors. Existing tokens require a new login when
switching to tenant mode. Tenant admins cannot administer platform clients;
platform tokens cannot access tenant business APIs.

## Configuration

Multi-tenancy is enabled by default. Set `MultiTenancy:Enabled=false` explicitly
only when you need legacy single-client operation. Configure production secrets
with environment variables or user secrets:

```text
MultiTenancy__Enabled=true
ConnectionStrings__MasterDb=Host=...;Database=meettomanage_master;Username=...;Password=...
ConnectionStrings__TenantServer=Host=...;Database=postgres;Username=...;Password=...;Maximum Pool Size=20
ConnectionStrings__TenantProvisioning=Host=...;Database=postgres;Username=...;Password=...
MultiTenancy__PlatformAdmin__Email=platform-admin@example.com
MultiTenancy__PlatformAdmin__PasswordHash=<BCrypt hash>
Jwt__SigningKey=<random secret of at least 32 bytes>
Database__MigrateOnStartup=true
Frontend__BaseUrl=https://your-frontend.example
Api__BaseUrl=https://your-api.example
```

`TenantProvisioning` is optional and falls back to `TenantServer`. Its role needs
CREATEDB and permission to assign new databases to `TenantServer`'s Username.
Both connections must address the same PostgreSQL server. Master credentials
need access to master and the privileges required for its migrations. Create
the shared master database/role before starting the app if the runtime role
cannot create databases.

Generate the platform password hash without starting the web host:

```powershell
dotnet run --project iucs.meettomanage.api -- platform-password-hash
```

Interactive password input is hidden. Use a long platform password. Client users
continue using the existing four-digit PIN policy.

In Development, if the explicit master/server connections are absent, a localhost
ReaderNestDb connection supplies the PostgreSQL server credentials. The master
database is separately named `meettomanage_tenants`; the existing ReaderNestDb is
not converted or imported. This fallback is never used in Production.

## Provisioning and management

Open `/platform/tenants` in the frontend. Sign in with platform credentials and
provide a code, name, administrator email and initial PIN. Provisioning registers
the tenant, creates `mt_<random GUID>`, applies business migrations, seeds the
existing roles/menus/settings and administrator, then activates it. No service
redeployment is required. Failed setups remain visible and retryable; provisioning
never drops databases. Retry using the intended original administrator details.
Tenant code/database mappings are immutable through the API.

The client Admin profile menu includes **Tenant administration**, opening the
separate Tenant Admin sign-in. Tenant Admins have a dedicated console navigation
and profile dropdown with **Tenant management**, **Menu management**, and **Tenant
configuration**. A client Admin token does not grant platform access.

Select a tenant on `/platform/menus` to create, edit, hide or remove portal menu
entries and set their permission gates and ordering. `/platform/configuration`
manages that tenant's organization, branding, notification, widget and SEO
settings. These operator endpoints explicitly bind a new scope to the selected
tenant and reuse the existing validated menu/settings services. Active and
suspended clients can be managed; incomplete provisioning must be repaired first.
Storage-provider changes and integration credentials retain their existing
dedicated client settings workflows.

On startup, master and Active tenant schemas migrate when MigrateOnStartup is
enabled. Seed reconciliation does not reset client administrator PINs to a global
seed. Workers discover Active tenants within 15 seconds and stop for suspended
tenants. Suspending a tenant rejects new requests and hub operations; it does not
undo work that had already started.

## Directory consistency

User creates/updates reserve normalized aliases in master before saving business
data. Unique constraints arbitrate concurrent registrations, including phones
written in different formats. One user ID belongs to exactly one tenant. Phone
normalization retains the application's existing last-ten-digits policy.

A transactional tenant outbox records user IDs alongside committed user changes.
Normal saves immediately mirror committed aliases/hash/deletion state. A tenant
worker retries durable messages every five seconds. Explicit transactions sync
only after their commit is visible. Advisory locks serialize consumers across
API processes; messages are removed only after master synchronization succeeds.
The outbox contains IDs rather than credential payloads. Hard-deletion flows
also enqueue directory reconciliation.

Two PostgreSQL databases are not one ordinary EF transaction. A rolled-back
business write can leave an alias reserved. Suspend the client and use **Sync
imported logins** to rebuild the directory from committed users and remove orphan
reservations. Master outages prevent new identity reservations; they never cause
a fallback to another client. Monitor synchronization warnings/errors in logs.

## Migrating live data

1. Provision a client and suspend it in the platform screen.
2. Import only its live data into its generated database. Preserve intra-client
   relationships; user IDs must also be globally unique. Do not overwrite master.
3. Apply business migrations, including `AddTenantLoginOutbox`.
4. Use **Sync imported logins** while suspended. Resolve global email/mobile
   conflicts, then activate and verify representative logins/business records.

The operator can find the generated database in master with:

```sql
SELECT "Id", "Code", "DatabaseName" FROM tenants WHERE "Code" = 'client-code';
```

Imported files need a separate migration: local files live under a tenant-ID
folder and portal S3 keys begin with `<tenant-id-in-N-format>-`. Copy files and
rewrite imported references into that namespace. Old flat keys are not
automatically reassigned to a tenant.

## Public links, storage and real-time access

Authenticated callers may keep existing `/api/...` paths. Anonymous client pages
use `/t/{tenantCode}/...` on frontend and API. Generated PIN-reset, guest, short,
payment and calendar links carry the prefix. The frontend preserves it across
navigation and API calls. Configure the frontend host to serve index.html for
these routes and preserve the full path when forwarding API requests.

The code supplies routing only. Endpoints retain existing capability-token, HMAC
or trusted-Jibri-IP checks. An authenticated URL cannot override its token's
tenant. Global `/login` remains available; unbound public-settings requests
receive an empty list for neutral branding. Client marketing/forms use prefixed
URLs. Configure gateway webhooks per client, for example:

```text
https://your-api.example/t/client-code/api/payments/webhook/razorpay
https://your-api.example/t/client-code/api/payments/webhook/cashfree
```

The Jibri observer URL also needs its client prefix. Regenerate or explicitly
redirect old unprefixed public links; the app will not guess which DB to search.

File services are scoped in tenant mode. Local roots/signing keys and S3 key
ownership are isolated. Provider choices/sync progress, email-template/dashboard
caches, classroom presence/state and SignalR groups are tenant scoped, even when
imported business IDs match. Hub scopes bind from validated principals and check
tenant activity on client invocations.

Legacy server monitoring, SSH controls, log readers and database-backup endpoints
are blocked for client sessions in tenant mode because their configured servers
and backup folders are shared across clients. Their menu entries are hidden.
Client-specific live-user/session views and reload broadcasts remain available.
The platform client-management screen does not expose shared infrastructure
controls; operate those through your existing server administration tools.

Existing business workers retain their schedules with bound scopes per tenant.
They remain process-local workers: coordinate job ownership when using multiple
API replicas. In-memory SignalR/classroom state still requires the existing
single-instance/sticky-session arrangement or a separate scale-out design.
The unbound `storage-migrate` CLI is rejected in tenant mode; use each client's
File storage sync page.

## Migrations and validation

Always specify one of the two EF contexts:

```powershell
dotnet ef database update --context MasterDbContext --project iucs.meettomanage.domain --startup-project iucs.meettomanage.api
dotnet ef database update --context MeetToManageDbContext --project iucs.meettomanage.domain --startup-project iucs.meettomanage.api
```

Design-time factories read `ConnectionStrings__MasterDb` or
`ConnectionStrings__ReaderNestDb`. Explicitly target the intended database.
The master SQL reference is `docs/master-registry-schema.sql`.

Validation includes existing backend/frontend suites, tenant resolution/runtime
tests and model/migration consistency checks. The HTTP integration script creates
uniquely named scratch databases on an explicitly supplied isolated PostgreSQL
server and removes those databases afterward:

```powershell
dotnet build iucs.meettomanage.api
python scripts/test_multitenancy.py --pg-bin "C:/Program Files/PostgreSQL/17/bin" --pg-port <isolated-test-port>
```

It checks provisioning, username/mobile login, signed tenant claims, cross-client
read/write rejection, platform separation, global email/mobile uniqueness, PIN
changes/hash synchronization, suspension/reactivation and directory rebuild.
The verified run passed 511 backend tests, 132 frontend tests, both builds and
52 PostgreSQL/HTTP checks, including selected-tenant menu/configuration edits,
operator access controls, registration races, shared-infrastructure access denial
and restart with one client database unavailable. Browser checks also cover the
Tenant Admin profile menu, menu creation, configuration saves and mobile layout.
External gateway callbacks, Jitsi video, S3 and live-data import require acceptance
checks in the deployment environment with prefixed URLs and isolated file keys.
