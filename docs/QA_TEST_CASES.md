# QA End-to-End Test Case Suite

This document is the executable companion to `TEST_STRATEGY.md` (which layer runs where) and
`UAT_PLAN.md` (client sign-off tracks). Those two are skeletons; this file is the actual test
case content — every portal, every screen, and the cross-cutting flows (auth, billing, live
classroom, security) that cut across all of them.

It was authored by reading the real route tree, controllers, permission matrix, background jobs,
and the project's own `Platform_Flow_Audit.md` — not written generically. Where the app has a
**known, already-documented gap** (a bug that's open, or a feature that's intentionally not built
yet), the test case says so explicitly in section 18, so you don't waste a cycle re-discovering it
and can instead verify it's still in the state the team expects.

## How to use this document

- Every test case is its own block with labeled fields — **Preconditions**, **Test Data** (where
  relevant), numbered **Test Steps**, **Expected Result**, and blank **Actual Result** /
  **Status** fields. Print or copy a section into your tracker of choice and fill in Actual
  Result / Status as you execute; that's what those two fields are for — they're intentionally
  empty in this document.
- "Mode" matters a lot in this app — most screens behave differently in **Demo mode** (no
  `VITE_API_BASE_URL`, mock data, nothing persists) vs **API mode** (real backend, real
  persistence, real error states). A screen that only "half-works" in demo mode is not a bug —
  check the Mode field before filing anything.
- Priority: **P0** = blocks release/core money or access-control path, **P1** = core feature
  correctness, **P2** = secondary feature/edge case, **P3** = cosmetic/low-risk polish.
- Run order within a section roughly follows the natural user journey (create → use → edit →
  delete → validation → permission-deny), so you can execute a section top-to-bottom without
  re-deriving state.
- Section 16 (Cross-Portal E2E) is the most valuable set to run first if you only have time for
  one thing — it exercises the real money/data path through five portals in sequence and will
  surface integration breaks that per-screen testing misses.
- Section 17 (Known Issues) are test cases you run to **confirm current behavior**, not to find
  new bugs — they encode facts already established in `Platform_Flow_Audit.md`. If one of them
  now passes differently than documented, that's a regression or a fix worth noting either way.
- This edition deepens coverage beyond the happy path: most screens now also carry field-level
  validation cases (required fields, length/format limits, invalid values), boundary cases
  (capacity limits, date-window edges, numeric edges), and duplicate/conflict cases, in addition
  to the permission-boundary and cross-portal cases from the first pass.

## Test case block template

Every test case in this document follows this exact shape:

```
### TC-<AREA>-<###> — <Title>

- **Portal / Module:** <where in the app>
- **Priority:** P0 | P1 | P2 | P3
- **Mode:** API | Demo | Both | N/A
- **Preconditions:** <state required before starting>
- **Test Data:** <specific values to use, if the case depends on specific data — omitted when not needed>
- **Test Steps:**
  1. ...
  2. ...
- **Expected Result:** <what must be true after the steps>
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass&nbsp;&nbsp;☐ Fail&nbsp;&nbsp;☐ Blocked&nbsp;&nbsp;☐ Not Run
```

## Test environment & setup

| Item | Value |
|---|---|
| Backend local run | `docker compose up -d` (Postgres, host port 5433) then `dotnet run --project iucs.readernest.api --launch-profile http` → `http://localhost:5288` |
| Frontend local run | `npm run dev` → `http://localhost:5173`; set `VITE_API_BASE_URL=http://localhost:5288` in `.env.development` for API mode, leave unset for Demo mode |
| Seeded admin login (API mode) | `admin@readernest.com` / `Admin@12345` (from `DatabaseInitializer` dev seed) |
| Demo mode login | `/login` shows a "Preview as (demo)" role selector — no real credentials needed, bypasses auth entirely |
| Health check | `GET /health` (unauthenticated) |
| API docs (dev only) | `http://localhost:5288/scalar` |
| Other roles (API mode) | Create via Admin → Users → Add User, or via a seeded permission preset; there is no seed data beyond the admin account, department payment accounts, settings, roles, and menus — you will need to build a test dataset (course → batch → teacher → demo booking → enrollment → parent → child) before most Teacher/Parent/Admission/Coordinator/Management cases are runnable end-to-end |
| Payment gateway test mode | Razorpay/Cashfree test credentials via Settings → Integrations, or leave unconfigured to exercise `SimulatedPaymentGateway` fallback intentionally (see TC-BIL cases) |

## Test data you'll need to build once (reused across many sections)

1. Two course categories (Phonics, Maths) — each maps to its own department payment account.
2. At least one course per category, one batch per course with a schedule generated (so
   holiday-skip and double-booking logic can be exercised).
3. At least two teacher accounts, two parent accounts each with one child, one admission-team
   account, one sub-admin account with a custom permission preset, one coordinator, one
   management account.
4. One demo booking taken through the full funnel to `Enrolled` status (needed for Section 16).
5. One package plan (subscription type) and one invoice in each of Pending/Overdue/Paid state.

## Test case ID conventions

| Prefix | Area |
|---|---|
| `TC-AUTH` | Authentication & session lifecycle |
| `TC-PERM` | Authorization / permission matrix (cross-portal) |
| `TC-ADM` | Admin portal |
| `TC-SUB` | Sub Admin portal |
| `TC-COR` | Coordinator portal |
| `TC-MGT` | Management portal |
| `TC-TCH` | Teacher portal |
| `TC-PAR` | Parent portal |
| `TC-STU` | Student portal |
| `TC-ADS` | Admission portal |
| `TC-MKT` | Public marketing site & store |
| `TC-CLS` | Live classroom / SignalR / Jitsi |
| `TC-BIL` | Billing & payments deep-dive (beyond what's in ADM/PAR/ADS) |
| `TC-SEC` | Security (AuthN/AuthZ abuse cases, injection, upload limits, webhooks) |
| `TC-JOB` | Background jobs (billing cycle, reminders, digests) |
| `TC-E2E` | Cross-portal, multi-role scenarios |
| `TC-GAP` | Known-issue / expected-current-behavior verification |

---

## 1. Authentication & Session Management (`TC-AUTH`)

Covers `Login.tsx`, `ForgotPassword.tsx`, `ResetPin.tsx`, `AuthController`, and the JWT lifecycle
(`Auth/JwtOptions.cs`, `OnTokenValidated`). Login uses email + 4-digit PIN, not a free-text
password field, in the UI — the underlying credential is still validated server-side as a
password/hash.

### TC-AUTH-001 — Successful login with valid credentials

- **Portal / Module:** Login (public)
- **Priority:** P0
- **Mode:** API
- **Preconditions:** A user account exists and is Active.
- **Test Data:** Valid email + correct 4-digit PIN for a seeded/test account.
- **Test Steps:**
  1. Navigate to `/login`.
  2. Enter the email.
  3. Enter the correct 4-digit PIN in the PIN boxes.
  4. Submit.
- **Expected Result:** 200 response; JWT stored client-side; user redirected to their role's `homePath`; `GET /api/auth/me` returns a matching profile plus the correct permission claims for their role/preset.
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

### TC-AUTH-002 — Login rejected with correct email, wrong PIN

- **Portal / Module:** Login (public)
- **Priority:** P0
- **Mode:** API
- **Preconditions:** A user account exists and is Active.
- **Test Data:** Valid email + an incorrect 4-digit PIN.
- **Test Steps:**
  1. Navigate to `/login`.
  2. Enter the valid email.
  3. Enter an incorrect PIN.
  4. Submit.
- **Expected Result:** Login rejected with a generic error message; no token stored; the message must not hint that the email is valid but the PIN is wrong (no partial-enumeration signal).
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

### TC-AUTH-003 — Login rejected for a non-existent email

- **Portal / Module:** Login (public)
- **Priority:** P1
- **Mode:** API
- **Preconditions:** None.
- **Test Data:** An email address with no matching account.
- **Test Steps:**
  1. Navigate to `/login`.
  2. Enter a non-existent email and any 4-digit PIN.
  3. Submit.
- **Expected Result:** Same generic error and response shape/timing as TC-AUTH-002 — response must not reveal whether the account exists.
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

### TC-AUTH-004 — PIN field only accepts 4 numeric digits

- **Portal / Module:** Login (public)
- **Priority:** P2
- **Mode:** Both
- **Preconditions:** None.
- **Test Steps:**
  1. On `/login`, attempt to type letters or symbols into a PIN box.
  2. Attempt to type a 5th digit into the 4-box PIN input.
  3. Paste a 4-digit numeric string into the first PIN box.
- **Expected Result:** Non-numeric input is rejected/ignored; input auto-advances between boxes and caps at 4 digits; pasting a full 4-digit code auto-fills all four boxes correctly.
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

### TC-AUTH-005 — Login blocked with empty email or incomplete PIN

- **Portal / Module:** Login (public)
- **Priority:** P2
- **Mode:** Both
- **Preconditions:** None.
- **Test Steps:**
  1. Leave the email field empty, enter a full PIN, submit.
  2. Enter a valid email, leave the PIN incomplete (e.g. 2 of 4 digits), submit.
- **Expected Result:** Both attempts are blocked client-side with a validation message; no request is sent to the server for either case.
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

### TC-AUTH-006 — Login rate limit enforced after repeated attempts

- **Portal / Module:** Login (public) / `POST /api/auth/login`
- **Priority:** P1
- **Mode:** API
- **Preconditions:** None.
- **Test Data:** A mix of valid/invalid login attempts, all from the same client IP.
- **Test Steps:**
  1. Submit 10 login attempts within 5 minutes from the same IP.
  2. Submit an 11th attempt within the same 5-minute window.
- **Expected Result:** The 11th (and any further) attempt within the window returns 429 immediately — fixed-window limiter with no queueing, so the response is instant, not delayed.
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

### TC-AUTH-007 — Deactivating a user invalidates their session immediately, not at token expiry

- **Portal / Module:** Auth / Admin → Users
- **Priority:** P0
- **Mode:** API
- **Preconditions:** A test user is logged in with a valid, unexpired JWT.
- **Test Steps:**
  1. In a separate Admin session, set the test user's status to Inactive (Admin → Users → status toggle).
  2. In the test user's original session (same still-valid token), make any authenticated request.
- **Expected Result:** The request is rejected with 401 on the very next call — `OnTokenValidated` re-checks DB status per-request, so deactivation takes effect immediately rather than waiting for the token's 8-hour lifetime to elapse.
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

### TC-AUTH-008 — Expired JWT forces re-login

- **Portal / Module:** Auth (any authenticated screen)
- **Priority:** P2
- **Mode:** API
- **Preconditions:** A logged-in session with a JWT that has passed its expiry (default 8h — shorten `Jwt:AccessTokenMinutes` in a test config to make this practical to execute).
- **Test Steps:**
  1. Wait for (or force) the JWT to expire.
  2. Make any authenticated request from the still-open UI.
- **Expected Result:** 401 returned; frontend auto-redirects to `/login` (per `apiFetch`'s 401 handling for any endpoint outside `/api/auth/*`).
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

### TC-AUTH-009 — Forgot PIN does not reveal account existence

- **Portal / Module:** Forgot Password (`/forgot-password`)
- **Priority:** P1
- **Mode:** API
- **Preconditions:** One email that has an account, one that doesn't.
- **Test Steps:**
  1. Submit "Forgot PIN" for the email that has an account.
  2. Submit "Forgot PIN" for the email that doesn't.
- **Expected Result:** Both return 204 with an identical response shape and comparable timing — no enumeration signal via this endpoint.
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

### TC-AUTH-010 — PIN reset token is single-use

- **Portal / Module:** Reset PIN (`/reset-pin?token=`)
- **Priority:** P1
- **Mode:** API
- **Preconditions:** A valid PIN-reset token has been issued (via TC-AUTH-009) and not yet used.
- **Test Steps:**
  1. Complete a PIN reset using the token, setting a new PIN.
  2. Attempt to use the same token again to set a different PIN.
- **Expected Result:** The first use succeeds and the new PIN takes effect for login. The second use is rejected — the token is consumed and cannot be replayed.
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

### TC-AUTH-011 — Invalid or garbage reset token handled gracefully

- **Portal / Module:** Reset PIN (`/reset-pin?token=`)
- **Priority:** P2
- **Mode:** API
- **Preconditions:** None.
- **Test Steps:**
  1. Open `/reset-pin?token=garbage-not-a-real-token`.
  2. Attempt to submit a new PIN.
- **Expected Result:** A graceful error state is shown, not a crash or blank screen; no PIN change occurs for any account.
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

### TC-AUTH-012 — Mismatched New PIN / Confirm PIN blocked client-side

- **Portal / Module:** Reset PIN (`/reset-pin?token=`)
- **Priority:** P2
- **Mode:** API
- **Preconditions:** A valid, unused reset token.
- **Test Steps:**
  1. Enter a New PIN.
  2. Enter a different value in Confirm PIN.
  3. Attempt to Save.
- **Expected Result:** Blocked client-side with a mismatch message; no request sent; no PIN changed.
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

### TC-AUTH-013 — Demo-mode role preview selector

- **Portal / Module:** Login (`/login`)
- **Priority:** P1
- **Mode:** Demo
- **Preconditions:** `VITE_API_BASE_URL` unset.
- **Test Steps:**
  1. Open `/login` in demo mode.
  2. Confirm a "Preview as (demo)" role selector is present in addition to the normal login form.
  3. Select a role (e.g. Teacher) and continue.
- **Expected Result:** Real auth is bypassed; user lands on that role's home path with `hasPermission()` always returning true throughout the session.
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

### TC-AUTH-014 — Portal-select is demo-only, unreachable in API mode

- **Portal / Module:** Portal Select (`/portal-select`)
- **Priority:** P1
- **Mode:** API
- **Preconditions:** `VITE_API_BASE_URL` set (API mode).
- **Test Steps:**
  1. Navigate directly to `/portal-select` by typing the URL.
- **Expected Result:** Redirected to `/login` — this screen is exploration-only and must not be reachable when a real backend is configured.
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

### TC-AUTH-015 — Cross-role deep link redirects to own home, not an error page

- **Portal / Module:** Auth guard (`RequireAuth`)
- **Priority:** P1
- **Mode:** API
- **Preconditions:** Logged in as Parent.
- **Test Steps:**
  1. While logged in as Parent, type `/admin` directly into the address bar.
- **Expected Result:** Redirected to the Parent's own `homePath` (e.g. `/parent`), not to a generic 403/error page.
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

### TC-AUTH-016 — Unauthenticated deep link redirects to login

- **Portal / Module:** Auth guard (`RequireAuth`)
- **Priority:** P2
- **Mode:** API
- **Preconditions:** No active session (logged out).
- **Test Steps:**
  1. Type any portal route directly into the address bar (e.g. `/teacher/classes`).
- **Expected Result:** Redirected to `/login`.
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

### TC-AUTH-017 — Sub Admin preset home path is honored

- **Portal / Module:** Auth / Sub Admin
- **Priority:** P1
- **Mode:** API
- **Preconditions:** A Sub Admin whose assigned preset's `homePath` points at `/admission` (the "preset" pattern, not the generic `/subadmin` shell).
- **Test Steps:**
  1. Log in as this Sub Admin.
- **Expected Result:** Landing page is `/admission`, not `/subadmin`; sidebar/nav reflect the admission preset's menu, not a generic sub-admin shell.
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

### TC-AUTH-018 — "Remember me" governs token persistence across browser restarts

- **Portal / Module:** Login (`/login`)
- **Priority:** P2
- **Mode:** API
- **Preconditions:** None.
- **Test Steps:**
  1. Log in with "Remember me" checked, close and reopen the browser, revisit the app.
  2. Log in with "Remember me" unchecked, close and reopen the browser, revisit the app.
- **Expected Result:** Behavior matches whatever persistence the checkbox is documented to control — verify actual current behavior for both cases rather than assuming; note any mismatch against the intended design.
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

### TC-AUTH-019 — SignalR query-string token accepted only on hub paths

- **Portal / Module:** Auth / SignalR (`ClassroomHub`)
- **Priority:** P2
- **Mode:** API
- **Preconditions:** A valid JWT.
- **Test Steps:**
  1. Connect to `/hubs/classroom` passing the JWT as `?access_token=<token>`.
  2. Call a normal (non-hub) REST endpoint passing the same JWT only as a `?access_token=` query string, with no `Authorization` header.
- **Expected Result:** Step 1 succeeds — this is the documented mechanism for SignalR, since browsers can't set auth headers on the WS handshake. Step 2 is rejected with 401 — query-string token auth must be scoped to `/hubs/*` only.
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

### TC-AUTH-020 — Logout clears session state completely

- **Portal / Module:** Auth (any portal, logout action)
- **Priority:** P1
- **Mode:** API
- **Preconditions:** Logged-in session.
- **Test Steps:**
  1. Trigger Logout from the app shell.
  2. Attempt to navigate back (browser back button) to a portal page.
  3. Attempt to call any authenticated endpoint via a request replaying the old token from browser history/dev tools.
- **Expected Result:** Token is cleared from `localStorage`; navigating back redirects to `/login`, not a cached authenticated view; a replayed pre-logout token still works until its own expiry (logout is client-side token removal, not server-side revocation) — confirm this is the actual/expected behavior rather than assuming server-side revocation exists.
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

### TC-AUTH-021 — Email is treated case-insensitively at login

- **Portal / Module:** Login (public)
- **Priority:** P2
- **Mode:** API
- **Preconditions:** An account exists with a lowercase email, e.g. `parent@readernest.com`.
- **Test Steps:**
  1. Log in using an uppercase/mixed-case version of the same email, e.g. `Parent@ReaderNest.com`, with the correct PIN.
- **Expected Result:** Login succeeds — email matching should not be case-sensitive; verify actual behavior since this is a common real-world gap.
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

### TC-AUTH-022 — Leading/trailing whitespace in email is trimmed

- **Portal / Module:** Login (public)
- **Priority:** P3
- **Mode:** API
- **Preconditions:** A valid account.
- **Test Steps:**
  1. Log in with a valid email that has an accidental leading/trailing space (e.g. from a copy-paste), correct PIN.
- **Expected Result:** Login succeeds — whitespace is trimmed before matching, not treated as part of the email.
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

---

## 2. Authorization / Permission Matrix (`TC-PERM`)

Covers `HasPermissionAttribute`/`PermissionAuthorization.cs` server-side and
`src/lib/permissions.ts` client-side. Core rule: **Admin bypasses every check implicitly**; every
other role needs an exact `perm:{Module}:{Action}` claim. Several controllers stack a role check
(`[Authorize(Roles=...)]`) *and* a permission check together specifically to prevent a shared
claim (e.g. Parent's own `BillingFinance:View`) from leaking into an unrelated admin-console
screen — this is a deliberate pattern, not redundancy, so test both layers independently.

### TC-PERM-001 — Admin bypasses all permission checks

- **Portal / Module:** Auth / any permission-gated endpoint
- **Priority:** P0
- **Mode:** API
- **Preconditions:** Admin account with no explicit permission claims manually set (Admin doesn't need any).
- **Test Steps:**
  1. As Admin, call `DELETE /api/users/{id}` for a test user.
- **Expected Result:** Succeeds — the Admin role bypasses `[HasPermission]` checks entirely regardless of claims present.
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

### TC-PERM-002 — Sub Admin without Create permission cannot create a user

- **Portal / Module:** Admin → Users / `POST /api/users`
- **Priority:** P0
- **Mode:** API
- **Preconditions:** Sub Admin with only `UserManagement:View` granted (no Create).
- **Test Steps:**
  1. As this Sub Admin, attempt `POST /api/users` with valid new-user data.
- **Expected Result:** 403 Forbidden; no user created.
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

### TC-PERM-003 — Teacher cannot list invoices regardless of stray claims

- **Portal / Module:** `GET /api/invoices`
- **Priority:** P0
- **Mode:** API
- **Preconditions:** A Teacher account (no `BillingFinance` claim expected).
- **Test Steps:**
  1. As Teacher, call `GET /api/invoices`.
- **Expected Result:** 403 — `InvoicesController` is role-restricted to Admin/SubAdmin/AdmissionTeam regardless of any permission claim Teacher might carry.
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

### TC-PERM-004 — Admission Team can exercise the full cash-confirmation flow

- **Portal / Module:** Admission → Payments / `GET cash-intents`, `POST cash-intents/{id}/confirm`
- **Priority:** P0
- **Mode:** API
- **Preconditions:** An Admission Team account with the seeded default role (`BillingFinance: View, Edit, Approve`); at least one pending cash intent.
- **Test Steps:**
  1. As Admission Team, call `GET /api/invoices/cash-intents`.
  2. Confirm one via `POST cash-intents/{id}/confirm`.
- **Expected Result:** Both succeed. This was previously broken (403 for this role on all billing endpoints) and is the subject of a real fix — regression-test it specifically, don't assume it stays fixed.
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

### TC-PERM-005 — Management cannot approve billing actions (by design)

- **Portal / Module:** `POST /api/invoices/cash-intents/{id}/confirm` and other `BillingFinance:Approve` actions
- **Priority:** P0
- **Mode:** API
- **Preconditions:** Management account (seeded default: `ReportsAnalytics:View` only, no `BillingFinance`).
- **Test Steps:**
  1. As Management, attempt to confirm a pending cash intent or approve a refund.
- **Expected Result:** 403. Management is intentionally view-only per the client spec — this is a deliberate design decision, not a defect. Verify it stays enforced as-is; do not "fix" without an explicit product decision.
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

### TC-PERM-006 — Role-preset edits propagate to already-logged-in users on next load

- **Portal / Module:** Admin → Permissions → Role Presets
- **Priority:** P1
- **Mode:** API
- **Preconditions:** A currently logged-in Teacher; Admin access to edit the "teacher" role preset.
- **Test Steps:**
  1. As Admin, grant an additional module to the "teacher" role preset.
  2. As the already-logged-in Teacher (same open session), reload the app.
- **Expected Result:** On reload, `GET /api/auth/me` reflects the new claim set. Confirm whether the change is picked up live within the already-open tab without reload, or requires reload — document actual behavior either way.
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

### TC-PERM-007 — Per-user permission grant to a non-SubAdmin role is blocked

- **Portal / Module:** Admin → Users → user detail → permissions
- **Priority:** P1
- **Mode:** API
- **Preconditions:** A Teacher user account.
- **Test Steps:**
  1. As Admin, attempt to hand-grant a `BillingFinance` permission to this Teacher directly via the Users screen (not via Role Presets).
- **Expected Result:** Rejected by `UserService.SetPermissionsAsync` — module permissions for any role other than SubAdmin can only be changed via the Role Presets matrix. Verify the UI blocks this, or the API 400s if bypassed via direct call.
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

### TC-PERM-008 — Sub Admin without Settings:View sees a no-access state on Integrations

- **Portal / Module:** Sub Admin → Integrations
- **Priority:** P1
- **Mode:** API
- **Preconditions:** Sub Admin preset lacking `Settings:View`.
- **Test Steps:**
  1. Open `/subadmin/integrations`.
- **Expected Result:** Renders an empty/no-access state, not the Integrations CRUD form and not a raw 403 crash.
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

### TC-PERM-009 — Sub Admin Reports tabs are gated per-module independently

- **Portal / Module:** Sub Admin → Reports
- **Priority:** P1
- **Mode:** API
- **Preconditions:** Sub Admin preset with access to some but not all report-relevant modules (e.g. `CourseBatchManagement:View` but not `ReportsAnalytics:View`).
- **Test Steps:**
  1. Open `/subadmin/reports`.
  2. Check each of the Attendance / Batch Occupancy / Batch Roster tabs individually.
- **Expected Result:** Only the tabs backed by a module this RM has permission for render real data; the rest show a permission-gated empty state — no all-or-nothing behavior across tabs.
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

### TC-PERM-010 — Parent cannot reach the org-wide session calendar endpoint

- **Portal / Module:** `GET /api/sessions`
- **Priority:** P1
- **Mode:** API
- **Preconditions:** Parent account.
- **Test Steps:**
  1. As Parent, call `GET /api/sessions` (the org-wide endpoint, not `/mine`).
- **Expected Result:** 403 — restricted to Admin/SubAdmin/AdmissionTeam roles regardless of any `SessionCalendarManagement:View` claim Parent carries for their own scoped screens.
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

### TC-PERM-011 — Teacher's own-session endpoint never returns another teacher's data

- **Portal / Module:** `GET /api/sessions/mine`
- **Priority:** P1
- **Mode:** API
- **Preconditions:** Two teacher accounts, each with at least one scheduled session.
- **Test Steps:**
  1. As Teacher A, call `GET /api/sessions/mine`.
  2. As Teacher B, call the same endpoint.
- **Expected Result:** Each response contains only sessions assigned to that specific teacher — zero cross-contamination.
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

### TC-PERM-012 — Parent's audit log is scoped to their own actions only

- **Portal / Module:** `GET /api/audit-logs`
- **Priority:** P1
- **Mode:** API
- **Preconditions:** Two parent accounts, each with at least one logged action.
- **Test Steps:**
  1. As Parent A, call `GET /api/audit-logs`.
- **Expected Result:** 200, but scoped to Parent A's own actor id only — never platform-wide entries, and never Parent B's entries.
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

### TC-PERM-013 — Admin/Settings:View sees the full platform-wide audit trail

- **Portal / Module:** `GET /api/audit-logs`
- **Priority:** P1
- **Mode:** API
- **Preconditions:** Admin account, or any role with `Settings:View`.
- **Test Steps:**
  1. Call `GET /api/audit-logs`.
- **Expected Result:** 200, platform-wide trail across all actors.
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

### TC-PERM-014 — Malformed token with no actor id is hard-restricted, not silently broadened

- **Portal / Module:** `GET /api/audit-logs`
- **Priority:** P1
- **Mode:** API
- **Preconditions:** A test harness able to present a token lacking the expected id claim.
- **Test Steps:**
  1. Call `GET /api/audit-logs` with such a token.
- **Expected Result:** 403/Forbid — not a 500, and not an empty-array pass-through that could mask the real cause.
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

### TC-PERM-015 — Teacher cannot edit role permission matrices

- **Portal / Module:** `PUT /api/roles/{id}`
- **Priority:** P2
- **Mode:** API
- **Preconditions:** Teacher account.
- **Test Steps:**
  1. As Teacher, attempt `PUT /api/roles/{id}` for any role.
- **Expected Result:** 403 — `RolesController` is gated on `Settings:Edit`, deliberately separate from `UserManagement`, since a role IS the permission matrix.
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

### TC-PERM-016 — Coordinator is confined to Sessions/Calendar/Leave scope

- **Portal / Module:** Various billing and user-management endpoints
- **Priority:** P1
- **Mode:** API
- **Preconditions:** Coordinator account with the seeded default preset.
- **Test Steps:**
  1. As Coordinator, attempt any `BillingFinance` endpoint.
  2. Attempt `POST/PUT/DELETE /api/users`.
- **Expected Result:** 403 across the board — Coordinator's scope is Sessions/Calendar/Leave only.
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

### TC-PERM-017 — Demo mode intentionally does not model permission denial

- **Portal / Module:** Any screen, demo mode
- **Priority:** P2
- **Mode:** Demo
- **Preconditions:** `VITE_API_BASE_URL` unset.
- **Test Steps:**
  1. Switch between preview roles via `/portal-select`.
  2. Attempt an action that would be permission-gated in API mode (e.g. delete a user as a low-privilege preview role).
- **Expected Result:** `useSession().hasPermission()` always returns true in demo mode — the action "succeeds" against mock data (no real persistence). This is intentional; don't file it as a bug.
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

### TC-PERM-018 — Permission matrix UI and server enforcement agree for every module/action pair

- **Portal / Module:** Admin → Permissions matrix vs live API behavior
- **Priority:** P1
- **Mode:** API
- **Preconditions:** A Sub Admin with a deliberately sparse, hand-picked matrix (a handful of modules granted, most withheld).
- **Test Steps:**
  1. For each module/action the matrix shows as **granted**, call the corresponding endpoint as this Sub Admin.
  2. For each module/action the matrix shows as **withheld**, call the corresponding endpoint.
- **Expected Result:** Every granted cell corresponds to a successful call; every withheld cell corresponds to a 403. No mismatch between what the UI displays as granted and what the server actually allows — this is the single most important consistency check in the whole permission system.
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

### TC-PERM-019 — Removing a permission mid-session blocks the very next matching call

- **Portal / Module:** Admin → Permissions, any granted endpoint
- **Priority:** P1
- **Mode:** API
- **Preconditions:** A Sub Admin currently able to perform some action (e.g. edit courses).
- **Test Steps:**
  1. As Admin, revoke `CourseBatchManagement:Edit` from this Sub Admin's preset.
  2. As the Sub Admin (reload to pick up new claims per TC-PERM-006), attempt to edit a course.
- **Expected Result:** 403 after reload — confirms revocation is enforced, not just granting.
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

### TC-PERM-020 — Deleted/deactivated role preset does not leave orphaned valid claims

- **Portal / Module:** Admin → Permissions → Role Presets
- **Priority:** P2
- **Mode:** API
- **Preconditions:** A custom role preset assigned to at least one user; Admin able to delete presets.
- **Test Steps:**
  1. Delete the preset while it's still assigned to a user (if the UI allows this at all).
  2. Have the affected user attempt a previously-granted action.
- **Expected Result:** Verify actual behavior — either deletion is blocked while in use with a clear message, or the affected user's claims degrade to a safe default (not to admin-level access). A preset deletion must never accidentally grant broader access than before.
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

---

## 3. Admin Portal (`TC-ADM`)

20 screens under `/admin/*`. Admin bypasses all permission checks, so these cases focus on
functional correctness, validation, and data integrity rather than access control (see Section 2
for the RBAC angle on the same endpoints from other roles).

### 3.1 Dashboard (`/admin`)

### TC-ADM-001 — Dashboard KPIs and charts load correctly

- **Portal / Module:** Admin → Dashboard
- **Priority:** P1
- **Mode:** API
- **Preconditions:** Test dataset with students, revenue, and sessions in place.
- **Test Steps:**
  1. Log in as Admin, load `/admin`.
- **Expected Result:** KPI tiles (students, revenue, conversion, attendance, renewal/refund rate, batch occupancy) and all 7 charts populate from `getDashboardSummary`; today's sessions list matches actual scheduled sessions for the current date.
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

### TC-ADM-002 — Personal meeting room mints and opens correctly

- **Portal / Module:** Admin → Dashboard
- **Priority:** P2
- **Mode:** API
- **Preconditions:** Logged in as Admin.
- **Test Steps:**
  1. Click "Personal Meeting".
- **Expected Result:** `GET /api/users/me/meeting-room` mints/returns the admin's own permanent room and opens it.
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

### TC-ADM-003 — Dashboard renders correctly in demo mode

- **Portal / Module:** Admin → Dashboard
- **Priority:** P2
- **Mode:** Demo
- **Preconditions:** `VITE_API_BASE_URL` unset.
- **Test Steps:**
  1. Load `/admin` with no backend configured.
- **Expected Result:** Mock KPI/chart data renders without errors or infinite loading states.
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

### TC-ADM-004 — Dashboard handles a zero-data (fresh) deployment

- **Portal / Module:** Admin → Dashboard
- **Priority:** P2
- **Mode:** API
- **Preconditions:** A freshly seeded environment with no students, sessions, or revenue yet.
- **Test Steps:**
  1. Load `/admin`.
- **Expected Result:** All charts render sensible zero/empty states — no NaN, no broken axes, no crash.
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

### 3.2 Users (`/admin/users`)

### TC-ADM-005 — Add User succeeds with valid data

- **Portal / Module:** Admin → Users
- **Priority:** P0
- **Mode:** API
- **Preconditions:** Logged in as Admin.
- **Test Data:** Unique email, valid name/phone, role = Teacher.
- **Test Steps:**
  1. Open Add User dialog.
  2. Fill required fields, select role = Teacher.
  3. Submit.
- **Expected Result:** 201; new teacher appears in the directory; credential email/WhatsApp dispatch is attempted; a temp password is generated.
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

### TC-ADM-006 — Add User rejects a duplicate active email

- **Portal / Module:** Admin → Users
- **Priority:** P1
- **Mode:** API
- **Preconditions:** An active account already uses the target email.
- **Test Steps:**
  1. Attempt to Add User with that same email.
- **Expected Result:** Rejected with a clear conflict error (409-mapped `ConflictException`); no duplicate created.
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

### TC-ADM-007 — Add User allows reuse of a soft-deleted account's email

- **Portal / Module:** Admin → Users
- **Priority:** P1
- **Mode:** API
- **Preconditions:** An email previously used by an account that has since been soft-deleted.
- **Test Steps:**
  1. Attempt to Add User with that same email.
- **Expected Result:** Succeeds — soft-deleted emails are documented as reusable.
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

### TC-ADM-008 — Add User validates required fields and formats

- **Portal / Module:** Admin → Users
- **Priority:** P1
- **Mode:** API
- **Test Steps:**
  1. Attempt submit with name empty.
  2. Attempt submit with an invalid email format (e.g. `not-an-email`).
  3. Attempt submit with an invalid/short phone number.
  4. Attempt submit with no role selected.
- **Expected Result:** Each case is blocked client-side (or 400s server-side if bypassed) with a field-specific message; no partial user record is created for any of the four cases.
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

### TC-ADM-009 — Edit Profile: role conversion succeeds with no operational history

- **Portal / Module:** Admin → Users
- **Priority:** P1
- **Mode:** API
- **Preconditions:** A Parent account with zero children attached.
- **Test Steps:**
  1. Edit Profile, change role Parent → Teacher.
- **Expected Result:** Succeeds; role and permissions update accordingly.
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

### TC-ADM-010 — Role conversion blocked when operational history exists

- **Portal / Module:** Admin → Users
- **Priority:** P1
- **Mode:** API
- **Preconditions:** A Parent account with at least one active child, or a Teacher with scheduled sessions.
- **Test Steps:**
  1. Attempt `PUT /{id}/role` to convert either account type.
- **Expected Result:** Rejected with a specific, non-generic error message explaining why (operational history exists).
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

### TC-ADM-011 — Delete Account soft-deletes and frees the email

- **Portal / Module:** Admin → Users
- **Priority:** P1
- **Mode:** API
- **Preconditions:** An active user with no critical dependent records blocking deletion.
- **Test Steps:**
  1. Delete Account, confirm.
  2. Verify the user no longer appears in active listings.
  3. Attempt to Add User again with the same email (cross-ref TC-ADM-007).
- **Expected Result:** User disappears from active listings; email becomes reusable.
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

### TC-ADM-012 — Deactivate/reactivate round-trip

- **Portal / Module:** Admin → Users
- **Priority:** P1
- **Mode:** API
- **Preconditions:** An active user with an open session (see TC-AUTH-007 for the token-side effect).
- **Test Steps:**
  1. Toggle status to Inactive.
  2. Toggle status back to Active.
  3. Have the user attempt to log in again.
- **Expected Result:** Toggle round-trips correctly in the UI; reactivated user can log in again immediately.
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

### TC-ADM-013 — Resend credentials generates a fresh temp password

- **Portal / Module:** Admin → Users
- **Priority:** P1
- **Mode:** API
- **Preconditions:** A user with a valid delivery channel (Email) configured.
- **Test Steps:**
  1. Resend credentials for the user.
  2. Attempt to log in with the user's previous temp password.
  3. Attempt to log in with the newly delivered temp password.
- **Expected Result:** New temp password generated and delivered; the old temp password no longer works; the new one does.
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

### TC-ADM-014 — Resend credentials surfaces delivery failure

- **Portal / Module:** Admin → Users
- **Priority:** P2
- **Mode:** API
- **Preconditions:** The configured delivery channel (e.g. Email/SMTP) is deliberately misconfigured in a test environment.
- **Test Steps:**
  1. Resend credentials for a user.
- **Expected Result:** Returns 400; UI surfaces the failure explicitly rather than silently claiming success.
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

### TC-ADM-015 — Student RM notes persist

- **Portal / Module:** Admin → Users → student detail
- **Priority:** P2
- **Mode:** API
- **Test Steps:**
  1. Open a student's detail dialog, edit RM enrollment notes, save.
  2. Reload the dialog.
- **Expected Result:** `PUT /students/{childId}/notes` persists; note reflects on reload.
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

### TC-ADM-016 — Directory search/filter scoping across tabs

- **Portal / Module:** Admin → Users
- **Priority:** P2
- **Mode:** API
- **Test Steps:**
  1. Search a term matching a user in the Parents tab while on the Teachers tab.
  2. Switch tabs and repeat with a term matching a user in the currently active tab.
- **Expected Result:** Filters scope correctly to the active tab; no cross-tab bleed of results.
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

### 3.3 Permissions (`/admin/permissions`)

### TC-ADM-017 — Toggle and save a single permission cell

- **Portal / Module:** Admin → Permissions
- **Priority:** P0
- **Mode:** API
- **Test Steps:**
  1. Toggle a module/action cell for a Sub Admin.
  2. Save.
  3. Reload the matrix.
- **Expected Result:** Persists via `PUT /{id}/permissions`; that Sub Admin's next `/api/auth/me` reflects the change; reload shows the saved state, not a reverted one.
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

### TC-ADM-018 — Apply a named preset overwrites the matrix exactly

- **Portal / Module:** Admin → Permissions
- **Priority:** P1
- **Mode:** API
- **Test Steps:**
  1. Use "Apply preset" to apply a named preset to an RM whose matrix currently differs from it.
  2. Save.
- **Expected Result:** Matrix updates to match the preset exactly (`PUT /{id}/permissions/preset/{preset}`) — no leftover cells from the prior state.
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

### TC-ADM-019 — Row/column/all toggles apply in one interaction

- **Portal / Module:** Admin → Permissions
- **Priority:** P1
- **Mode:** API
- **Test Steps:**
  1. Toggle an entire row (module).
  2. Toggle an entire column (action).
  3. Toggle "all".
  4. Save once after each.
- **Expected Result:** All affected cells update in the UI in a single interaction each time; a single Save persists the full set correctly.
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

### TC-ADM-020 — Reset discards unsaved changes

- **Portal / Module:** Admin → Permissions
- **Priority:** P1
- **Mode:** API
- **Test Steps:**
  1. Make several unsaved changes to the matrix.
  2. Click Reset.
- **Expected Result:** Matrix reverts fully to the last-saved state; no partial/half-saved state remains.
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

### TC-ADM-021 — Create a new role preset with a default landing page

- **Portal / Module:** Admin → Permissions → Role Presets
- **Priority:** P1
- **Mode:** API
- **Test Steps:**
  1. Create a new named preset with a custom matrix and a default landing page.
- **Expected Result:** `POST /api/roles` succeeds; the new preset appears in the "Apply preset" dropdown and (if applicable) in the Add-User role picker.
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

### TC-ADM-022 — Edit an existing preset propagates to assigned users

- **Portal / Module:** Admin → Permissions → Role Presets
- **Priority:** P1
- **Mode:** API
- **Test Steps:**
  1. Edit the "teacher" preset's matrix, save.
- **Expected Result:** `PUT /api/roles/{id}` persists (see TC-PERM-006 for the propagation-to-logged-in-users angle).
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

### TC-ADM-023 — Delete an unused vs. in-use preset

- **Portal / Module:** Admin → Permissions → Role Presets
- **Priority:** P2
- **Mode:** API
- **Test Steps:**
  1. Delete a preset assigned to zero users.
  2. Attempt to delete a preset currently assigned to at least one user.
- **Expected Result:** Unused preset deletes cleanly. In-use preset either blocks deletion with a clear message or reassigns gracefully — verify actual behavior, don't assume.
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

### 3.4 Courses (`/admin/courses`)

### TC-ADM-024 — Create Course with valid data

- **Portal / Module:** Admin → Courses
- **Priority:** P0
- **Mode:** API
- **Test Data:** Name, category = Phonics, duration = 45 min, price = 5000, total sessions = 20.
- **Test Steps:**
  1. Create Course with the above data.
- **Expected Result:** 201; appears in catalogue table with correct fields.
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

### TC-ADM-025 — Create Course rejects invalid numeric fields

- **Portal / Module:** Admin → Courses
- **Priority:** P1
- **Mode:** API
- **Test Steps:**
  1. Attempt to create a course with a negative price.
  2. Attempt to create a course with zero total sessions.
  3. Attempt to create a course with a duration outside 30/45/60.
- **Expected Result:** Each is rejected (client-side validation and/or server `DomainValidationException` → 400); no course created for any of the three.
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

### TC-ADM-026 — Create Course requires a category

- **Portal / Module:** Admin → Courses
- **Priority:** P2
- **Mode:** API
- **Test Steps:**
  1. Attempt to submit Create Course with no category selected.
- **Expected Result:** Blocked with a required-field message.
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

### TC-ADM-027 — New category is immediately available for course creation

- **Portal / Module:** Admin → Courses
- **Priority:** P2
- **Mode:** API
- **Test Steps:**
  1. Create a new course category.
  2. Immediately open Create Course and check the category picker.
- **Expected Result:** New category appears in the picker without requiring a page reload (or, if a reload is genuinely required, that should be verified and noted as the real behavior).
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

### TC-ADM-028 — includeInactive filter toggles archived courses

- **Portal / Module:** Admin → Courses
- **Priority:** P2
- **Mode:** API
- **Preconditions:** At least one inactive/archived course exists.
- **Test Steps:**
  1. Toggle `includeInactive` on and off.
- **Expected Result:** Inactive courses show/hide accordingly.
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

### TC-ADM-029 — Course table search/sort/pagination accuracy

- **Portal / Module:** Admin → Courses
- **Priority:** P2
- **Mode:** API
- **Test Steps:**
  1. Search/sort/paginate the courses table with a dataset spanning multiple pages.
- **Expected Result:** Correct results at each page; revenue/enrolled columns match actual batch/enrollment data, not stale or cached figures.
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

### 3.5 Batches (`/admin/batches`)

### TC-ADM-030 — Create a new batch

- **Portal / Module:** Admin → Batches
- **Priority:** P0
- **Mode:** API
- **Test Steps:**
  1. Create a batch: select course, set capacity, assign a teacher.
- **Expected Result:** 201; appears under the correct tab (Active/Dormant/Upcoming) by status.
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

### TC-ADM-031 — Generate Schedule skips academic-calendar holidays

- **Portal / Module:** Admin → Batches
- **Priority:** P0
- **Mode:** API
- **Preconditions:** A holiday exists on the Academic Calendar within the schedule's date window.
- **Test Steps:**
  1. Generate Schedule: pick a start date, weekdays, and time whose window overlaps the holiday.
- **Expected Result:** Sessions are auto-created on the chosen weekdays but skip the holiday date(s) entirely — no session lands on a holiday.
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

### TC-ADM-032 — Generate Schedule rejects a double-booked teacher

- **Portal / Module:** Admin → Batches
- **Priority:** P0
- **Mode:** API
- **Preconditions:** A teacher already has a session at a given date/time from another batch.
- **Test Steps:**
  1. Generate Schedule for a different batch assigned to the same teacher, overlapping that date/time.
- **Expected Result:** Rejected — double-booking a teacher is a hard system rule, not just a UI warning.
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

### TC-ADM-033 — Assigning a student beyond batch capacity is rejected

- **Portal / Module:** Admin → Batches
- **Priority:** P1
- **Mode:** API
- **Preconditions:** A batch already at full capacity.
- **Test Steps:**
  1. Attempt to assign one more student to the batch.
- **Expected Result:** Rejected (`POST enrollments` blocked at capacity); clear error shown; no over-enrollment persisted.
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

### TC-ADM-034 — Removing a student frees a seat immediately

- **Portal / Module:** Admin → Batches
- **Priority:** P1
- **Mode:** API
- **Preconditions:** A batch at capacity.
- **Test Steps:**
  1. Remove one student.
  2. Immediately assign a different student up to the freed capacity.
- **Expected Result:** The removal frees the seat immediately; the new assignment succeeds without needing a reload/delay.
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

### TC-ADM-035 — Manual batch status change reflects across dashboard tabs

- **Portal / Module:** Admin → Batches
- **Priority:** P1
- **Mode:** API
- **Test Steps:**
  1. Manually change a batch's status (e.g. Active → Dormant).
- **Expected Result:** Updates and the batch reflects in the correct dashboard tab immediately.
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

### TC-ADM-036 — Batch auto-moves to Dormant on course completion

- **Portal / Module:** Admin → Batches / Sessions
- **Priority:** P1
- **Mode:** API
- **Preconditions:** A batch on its final scheduled session for the course.
- **Test Steps:**
  1. Mark the batch's final session complete via `sessions/{id}/complete`.
- **Expected Result:** Batch auto-moves to Dormant without any manual status change; this is also what starts the 15-day recording window (cross-check TC-PAR and TC-GAP-003).
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

### TC-ADM-037 — Unassigned-students list excludes ineligible children

- **Portal / Module:** Admin → Batches
- **Priority:** P2
- **Mode:** API
- **Preconditions:** Children in multiple states: approved+active+unassigned, already-in-a-batch, pending enrollment form, rejected form.
- **Test Steps:**
  1. Open the "unassigned students" list while managing a batch.
- **Expected Result:** Only approved, active children not already in any batch for that course appear — no duplicates, no pending/rejected-form children.
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

### TC-ADM-038 — Batch capacity cannot be edited below current enrollment

- **Portal / Module:** Admin → Batches
- **Priority:** P2
- **Mode:** API
- **Preconditions:** A batch with, e.g., 8 students enrolled.
- **Test Steps:**
  1. Attempt to edit the batch's capacity down to 5.
- **Expected Result:** Verify actual behavior — either blocked with a clear message, or allowed but the batch simply shows as over capacity going forward. Either is defensible; a silent data-integrity break (e.g. students getting dropped) is not.
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

### 3.6 Academic Calendar (`/admin/calendar`)

### TC-ADM-039 — Add a Holiday and confirm schedule-generation respects it

- **Portal / Module:** Admin → Academic Calendar
- **Priority:** P1
- **Mode:** API
- **Test Steps:**
  1. Add a Holiday (date + name).
  2. Run Generate Schedule for a batch whose window includes that date (cross-ref TC-ADM-031).
- **Expected Result:** Holiday appears on the calendar; subsequent schedule generation skips it.
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

### TC-ADM-040 — Deleting a Holiday does not retroactively affect existing sessions

- **Portal / Module:** Admin → Academic Calendar
- **Priority:** P1
- **Mode:** API
- **Preconditions:** A Holiday exists and a schedule was already generated around it.
- **Test Steps:**
  1. Delete the Holiday.
  2. Check the previously-generated schedule.
- **Expected Result:** Deletion succeeds; already-generated sessions are unaffected (no session is retroactively created/deleted on that date without an explicit re-generate action).
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

### TC-ADM-041 — Duplicate holiday date is handled sensibly

- **Portal / Module:** Admin → Academic Calendar
- **Priority:** P2
- **Mode:** API
- **Preconditions:** A holiday already exists on a given date.
- **Test Steps:**
  1. Attempt to add another holiday on the same date.
- **Expected Result:** Either rejected as a duplicate, or allowed as a second named entry on the same date without breaking schedule-generation's skip logic — verify no crash either way.
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

### TC-ADM-042 — Clicking a calendar session opens accurate detail

- **Portal / Module:** Admin → Academic Calendar
- **Priority:** P2
- **Mode:** API
- **Test Steps:**
  1. Click a session on the calendar.
  2. Compare the detail dialog to the same session's row on `/admin/sessions`.
- **Expected Result:** Opens the correct session detail dialog with data matching the Sessions screen exactly.
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

### TC-ADM-043 — Calendar color-coding is legend-consistent

- **Portal / Module:** Admin → Academic Calendar
- **Priority:** P2
- **Mode:** API
- **Preconditions:** A month with sessions, holidays, and at least one approved leave entry all present.
- **Test Steps:**
  1. Visually inspect the calendar for that month.
- **Expected Result:** Sessions, holidays, and leave entries are visually distinct and match the on-screen legend.
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

### 3.7 Sessions (`/admin/sessions`)

### TC-ADM-044 — Schedule a one-off session

- **Portal / Module:** Admin → Sessions
- **Priority:** P0
- **Mode:** API
- **Test Steps:**
  1. Schedule Session: type, batch, teacher, date, time, duration.
- **Expected Result:** 201; appears in the sessions table with correct details.
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

### TC-ADM-045 — Scheduling rejects a double-booked teacher

- **Portal / Module:** Admin → Sessions
- **Priority:** P1
- **Mode:** API
- **Test Steps:**
  1. Schedule a session for a teacher who already has one at that exact date/time.
- **Expected Result:** Rejected per the same double-booking rule enforced elsewhere (cross-ref TC-ADM-032).
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

### TC-ADM-046 — Reschedule to a new date/time re-validates double-booking

- **Portal / Module:** Admin → Sessions
- **Priority:** P1
- **Mode:** API
- **Test Steps:**
  1. Reschedule a session to a new date/time.
  2. Attempt to reschedule a different session for the same teacher into the now-freed original slot vs. a slot that conflicts with the just-moved session.
- **Expected Result:** `POST {id}/reschedule` succeeds for the non-conflicting case; the old slot is freed; the conflicting case is rejected.
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

### TC-ADM-047 — Cancel a session removes it from active calculations

- **Portal / Module:** Admin → Sessions
- **Priority:** P1
- **Mode:** API
- **Test Steps:**
  1. Cancel a scheduled session, confirm.
  2. Check attendance/payout calculations for the affected teacher/batch.
- **Expected Result:** `POST {id}/cancel` succeeds; session removed from active calendars; does not double-count in attendance or payout calculations.
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

### TC-ADM-048 — Sessions table status filter accuracy

- **Portal / Module:** Admin → Sessions
- **Priority:** P2
- **Mode:** API
- **Test Steps:**
  1. Filter by each status (Scheduled/Completed/Cancelled/etc) in turn.
- **Expected Result:** Correct subset of sessions shown for each filter value.
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

### TC-ADM-049 — Cannot reschedule or cancel a completed session

- **Portal / Module:** Admin → Sessions
- **Priority:** P2
- **Mode:** API
- **Preconditions:** A session already marked Completed.
- **Test Steps:**
  1. Attempt Reschedule.
  2. Attempt Cancel.
- **Expected Result:** Both blocked with a clear message — completed history cannot be rewritten.
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

### TC-ADM-050 — Session date/time validation rejects impossible values

- **Portal / Module:** Admin → Sessions
- **Priority:** P2
- **Mode:** API
- **Test Steps:**
  1. Attempt to schedule a session in the past.
  2. Attempt to schedule with an end time before the start time (if the form allows independent entry).
- **Expected Result:** Both rejected with a clear validation message.
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

### 3.8 Resources (`/admin/resources`)

### TC-ADM-051 — Upload a resource within size limit

- **Portal / Module:** Admin → Resources
- **Priority:** P0
- **Mode:** API
- **Test Steps:**
  1. Upload a book/worksheet/recording file under a course category and batch, within the 100MB cap.
- **Expected Result:** 201; appears in the library with correct type/visibility.
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

### TC-ADM-052 — Upload rejects files over 100MB

- **Portal / Module:** Admin → Resources
- **Priority:** P1
- **Mode:** API
- **Test Steps:**
  1. Attempt to upload a file larger than 100MB.
- **Expected Result:** Rejected — the cap is enforced server-side, not just a UI hint (verify by bypassing the UI check if possible).
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

### TC-ADM-053 — Upload rejects empty (0-byte) files

- **Portal / Module:** Admin → Resources
- **Priority:** P1
- **Mode:** API
- **Test Steps:**
  1. Attempt to upload a 0-byte file.
- **Expected Result:** Rejected with 400 (`DomainValidationException`).
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

### TC-ADM-054 — Toggle Downloadable off blocks download while preview still works

- **Portal / Module:** Admin → Resources
- **Priority:** P1
- **Mode:** API
- **Preconditions:** A book-type resource, currently Downloadable.
- **Test Steps:**
  1. Toggle Downloadable off.
  2. As a Parent with access, attempt to view vs. download it.
- **Expected Result:** View/preview still works (books are documented as view-only by design); parent-facing download is blocked.
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

### TC-ADM-055 — Toggle Visible-to-Parents off hides it from the parent portal only

- **Portal / Module:** Admin → Resources
- **Priority:** P1
- **Mode:** API
- **Test Steps:**
  1. Toggle Visible-to-Parents off for a resource.
  2. Check Parent → Resources for the relevant batch.
  3. Check the Admin library.
- **Expected Result:** Resource disappears from the parent-facing screen but remains visible in the Admin library.
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

### TC-ADM-056 — Upload with a path-traversal-style filename is stored safely

- **Portal / Module:** Admin → Resources
- **Priority:** P1
- **Mode:** API
- **Test Data:** A file named e.g. `../../etc/passwd` or `..\\..\\config.json`.
- **Test Steps:**
  1. Upload a file using this crafted filename.
- **Expected Result:** Stored under a server-generated GUID name, not the literal filename — no path traversal, no overwrite of unrelated files (cross-ref TC-SEC-004).
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

### TC-ADM-057 — Metadata edit does not change the underlying stored file

- **Portal / Module:** Admin → Resources
- **Priority:** P2
- **Mode:** API
- **Test Steps:**
  1. Edit a resource's title/description metadata.
  2. Re-download the file.
- **Expected Result:** Metadata updates in listings; the actual file content is unchanged and still downloads correctly.
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

### 3.9 Billing (`/admin/billing`)

### TC-ADM-058 — Confirm a pending cash intent

- **Portal / Module:** Admin → Billing → Needs Your Action
- **Priority:** P0
- **Mode:** API
- **Preconditions:** At least one pending cash-payment intent from a parent's Pay Now → Cash action.
- **Test Steps:**
  1. Open the Cash Confirmation panel.
  2. Confirm the pending intent.
- **Expected Result:** Transaction settles, a receipt is generated, payment is applied to the invoice, and fee suspension auto-lifts if this was the final due payment.
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

### TC-ADM-059 — Reject a pending cash intent

- **Portal / Module:** Admin → Billing → Needs Your Action
- **Priority:** P0
- **Mode:** API
- **Test Steps:**
  1. Reject a pending cash intent.
- **Expected Result:** Intent marked rejected; invoice remains unpaid/overdue as appropriate; no receipt generated.
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

### TC-ADM-060 — Manual payment dedupes against an existing pending cash intent

- **Portal / Module:** Admin → Billing
- **Priority:** P1
- **Mode:** API
- **Preconditions:** An invoice with a matching pending cash intent already open.
- **Test Steps:**
  1. Record a manual payment on the same invoice for the same amount.
- **Expected Result:** The manual entry settles the existing pending row rather than creating a duplicate — this was a fixed regression; verify via the transaction list, not just the invoice total (cross-ref TC-GAP-002).
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

### TC-ADM-061 — Record Payment validates amount

- **Portal / Module:** Admin → Billing
- **Priority:** P1
- **Mode:** API
- **Test Steps:**
  1. Attempt to record a payment of ₹0 or a negative amount.
  2. Attempt to record a payment greater than the invoice's remaining balance.
- **Expected Result:** Both rejected with a clear validation message, or the overpayment case is explicitly handled (e.g. credited/flagged) rather than silently accepted — verify actual behavior for the overpayment case specifically.
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

### TC-ADM-062 — Request a refund on a paid invoice

- **Portal / Module:** Admin → Billing
- **Priority:** P1
- **Mode:** API
- **Test Steps:**
  1. Request a refund with amount + reason on a paid invoice.
- **Expected Result:** Refund request created in Pending state; appears in the Refund Requests panel.
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

### TC-ADM-063 — Approve a refund on a gateway-paid invoice triggers a real gateway call

- **Portal / Module:** Admin → Billing
- **Priority:** P0
- **Mode:** API
- **Preconditions:** An invoice paid via Razorpay or Cashfree (test credentials).
- **Test Steps:**
  1. Approve the pending refund request.
- **Expected Result:** A real refund API call is made to the gateway (not a no-op); `Refund.GatewayRefundId` populated on success; a gateway-side failure surfaces as a real error, not a false "Processed" status.
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

### TC-ADM-064 — Approve a refund on a cash-paid invoice skips the gateway

- **Portal / Module:** Admin → Billing
- **Priority:** P1
- **Mode:** API
- **Preconditions:** An invoice paid via confirmed cash.
- **Test Steps:**
  1. Approve the pending refund request.
- **Expected Result:** No gateway call made; refund marked Processed via manual bookkeeping only.
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

### TC-ADM-065 — Reject a refund request

- **Portal / Module:** Admin → Billing
- **Priority:** P2
- **Mode:** API
- **Test Steps:**
  1. Reject a pending refund request instead of approving it.
- **Expected Result:** Request marked rejected; no gateway call made; no change to the invoice's paid state.
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

### TC-ADM-066 — Download Receipt produces accurate, print-ready output

- **Portal / Module:** Admin → Billing
- **Priority:** P1
- **Mode:** API
- **Test Steps:**
  1. Download Receipt for a fully settled invoice.
- **Expected Result:** Produces a print-ready document with correct amounts/dates matching the invoice's actual transaction history.
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

### TC-ADM-067 — Payment history reflects multiple partial payments correctly

- **Portal / Module:** Admin → Billing
- **Priority:** P1
- **Mode:** API
- **Preconditions:** An invoice paid via two or more partial payments.
- **Test Steps:**
  1. Open the invoice detail dialog, view payment history.
- **Expected Result:** All transactions listed in order with a correct running balance after each.
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

### 3.10 Packages (`/admin/packages`)

### TC-ADM-068 — Create a Package Plan

- **Portal / Module:** Admin → Packages
- **Priority:** P1
- **Mode:** API
- **Test Steps:**
  1. Create a Package Plan: subscription type, billing cycle, price, session count.
- **Expected Result:** 201; appears in the Plans tab.
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

### TC-ADM-069 — Package Plan rejects invalid price/session count

- **Portal / Module:** Admin → Packages
- **Priority:** P2
- **Mode:** API
- **Test Steps:**
  1. Attempt to create a plan with a negative or zero price.
  2. Attempt to create a plan with zero session count for a session-based type.
- **Expected Result:** Both rejected with clear validation messages.
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

### TC-ADM-070 — Start a Subscription against a plan

- **Portal / Module:** Admin → Packages
- **Priority:** P1
- **Mode:** API
- **Test Steps:**
  1. Start a Subscription for a student against a plan, with a start date.
- **Expected Result:** `POST /api/subscriptions`; `NextBillingAtUtc` set correctly per the plan's cycle.
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

### TC-ADM-071 — Cancel an active subscription stops future billing

- **Portal / Module:** Admin → Packages
- **Priority:** P1
- **Mode:** API
- **Test Steps:**
  1. Cancel an active subscription.
  2. Simulate/wait for the billing background job's next cycle.
- **Expected Result:** `POST {id}/cancel` succeeds; the job no longer generates future invoices for this subscription (cross-ref TC-JOB-001).
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

### TC-ADM-072 — Renew a cancelled subscription resumes auto-billing

- **Portal / Module:** Admin → Packages
- **Priority:** P2
- **Mode:** API
- **Test Steps:**
  1. Renew a cancelled/lapsed subscription.
- **Expected Result:** `POST {id}/renew`; auto-billing resumes; next invoice generated on the plan's schedule.
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

### TC-ADM-073 — One-time plan bills exactly once

- **Portal / Module:** Admin → Packages
- **Priority:** P2
- **Mode:** API
- **Test Steps:**
  1. Create a one-time (non-recurring) plan and subscribe a student.
  2. Let two full billing-cycle windows pass (or simulate).
- **Expected Result:** Bills once; `NextBillingAtUtc` nulls out afterward; the background job does not attempt to re-bill it on the second window.
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

### 3.11 Payment Mapping (`/admin/payment-mapping`)

### TC-ADM-074 — Department account cards show correct scoped data

- **Portal / Module:** Admin → Payment Mapping
- **Priority:** P0
- **Mode:** API
- **Test Steps:**
  1. View the Phonics and Maths department cards.
- **Expected Result:** Each shows correct gateway provider, active state, and recent transactions for that department only — no cross-department bleed.
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

### TC-ADM-075 — Misconfigured gateway wiring surfaces the simulated-fallback state visibly

- **Portal / Module:** Admin → Payment Mapping
- **Priority:** P0
- **Mode:** API
- **Test Steps:**
  1. Edit a department account's provider to a value with no matching live gateway configured.
- **Expected Result:** Confirm dialog warns, or the system's fallback to `SimulatedPaymentGateway` is visibly flagged in the UI — not silent (this was a real, since-fixed defect; cross-ref TC-GAP-001).
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

### TC-ADM-076 — Default department routing sends each department to its correct gateway

- **Portal / Module:** Admin → Payment Mapping
- **Priority:** P1
- **Mode:** API
- **Preconditions:** Phonics → razorpay, Maths → cashfree (documented default).
- **Test Steps:**
  1. Have a Phonics-department parent pay an invoice.
  2. Have a Maths-department parent pay an invoice.
- **Expected Result:** Each payment routes to its correct configured gateway.
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

### TC-ADM-077 — Manual parent→account mapping overrides department default

- **Portal / Module:** Admin → Payment Mapping
- **Priority:** P1
- **Mode:** API
- **Test Steps:**
  1. Assign a specific parent to a payment account different from their department's default.
  2. Have that parent pay an invoice.
- **Expected Result:** Payment routes per the manual mapping, overriding the department default.
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

### 3.12 Payouts (`/admin/payouts`)

### TC-ADM-078 — Payouts table filters correctly

- **Portal / Module:** Admin → Payouts
- **Priority:** P1
- **Mode:** API
- **Test Steps:**
  1. Filter by year, month, and teacher in various combinations.
- **Expected Result:** Correct scoped results for every filter combination.
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

### TC-ADM-079 — Finalize a monthly payout locks the total

- **Portal / Module:** Admin → Payouts
- **Priority:** P0
- **Mode:** API
- **Test Steps:**
  1. Finalize a teacher's monthly payout, confirm.
  2. Attempt to affect the total afterward (e.g. a late session-completion edit for that same period).
- **Expected Result:** Total locks — no further session-accrual changes affect it after finalization; statement is emailed to the teacher.
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

### TC-ADM-080 — Finalizing an already-finalized payout is idempotent

- **Portal / Module:** Admin → Payouts
- **Priority:** P1
- **Mode:** API
- **Test Steps:**
  1. Attempt to finalize a payout that's already finalized.
- **Expected Result:** Blocked or a no-op — no duplicate finalization, no duplicate statement email.
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

### TC-ADM-081 — Mark Paid updates status and is reflected on the teacher's own screen

- **Portal / Module:** Admin → Payouts
- **Priority:** P1
- **Mode:** API
- **Test Steps:**
  1. Mark Paid on a finalized payout.
  2. Log in as that teacher, check their own Payout screen.
- **Expected Result:** Status updates on both screens consistently.
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

### TC-ADM-082 — Export Payouts CSV matches current filter

- **Portal / Module:** Admin → Payouts
- **Priority:** P2
- **Mode:** API
- **Test Steps:**
  1. Apply a filter, export CSV.
- **Expected Result:** Downloaded file's rows match exactly what the filtered table shows on screen.
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

### TC-ADM-083 — No-show payout math differs correctly by no-show type

- **Portal / Module:** Admin → Payouts / Sessions
- **Priority:** P1
- **Mode:** API
- **Test Steps:**
  1. Mark a teacher no-show on one session for a teacher.
  2. Mark a student no-show on a different session for the same teacher.
  3. Compare that teacher's payout for the period.
- **Expected Result:** Teacher no-show applies a payout deduction; student no-show instead credits a "waiting amount" — verify these apply distinctly, not identically.
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

### TC-ADM-084 — Payout rate card changes apply to newly completed sessions, not retroactively

- **Portal / Module:** Admin → Payouts / Settings → Payroll
- **Priority:** P2
- **Mode:** API
- **Preconditions:** A teacher with an already-completed session at the old rate.
- **Test Steps:**
  1. Change the teacher's payout rate card in Settings → Payroll.
  2. Complete a new session for that teacher.
  3. Check the payout contribution of the old session vs. the new one.
- **Expected Result:** The old session's payout contribution stays at the rate in effect when it was completed; only the new session reflects the updated rate.
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

### 3.13 Fee Suspension (`/admin/fee-suspension`)

### TC-ADM-085 — Suspended accounts list reflects auto-suspension

- **Portal / Module:** Admin → Fee Suspension
- **Priority:** P0
- **Mode:** API
- **Preconditions:** The billing background job has auto-suspended an overdue parent (TC-JOB-003).
- **Test Steps:**
  1. View the suspended accounts list.
- **Expected Result:** The account appears with the correct overdue invoice reference.
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

### TC-ADM-086 — Restore Access unblocks the parent immediately

- **Portal / Module:** Admin → Fee Suspension
- **Priority:** P0
- **Mode:** API
- **Test Steps:**
  1. Restore Access on a suspended account.
  2. As that parent, attempt to view Resources immediately.
- **Expected Result:** `POST suspensions/{id}/lift`; content/session access unblocks on the very next request, no delay.
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

### TC-ADM-087 — Full payment auto-lifts suspension without manual action

- **Portal / Module:** Admin → Fee Suspension / Parent → Billing
- **Priority:** P1
- **Mode:** API
- **Test Steps:**
  1. Have the suspended parent pay the overdue invoice in full via Pay Now, without any admin action.
- **Expected Result:** Suspension auto-lifts — verify this happens without needing the manual Restore step (cross-ref TC-PAR-012).
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

### TC-ADM-088 — Partial payment does not lift suspension

- **Portal / Module:** Admin → Fee Suspension
- **Priority:** P1
- **Mode:** API
- **Test Steps:**
  1. Have the suspended parent make a partial payment toward the overdue invoice, less than the full amount.
- **Expected Result:** Suspension remains active — only full payment auto-lifts it.
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

### 3.14 Reports (`/admin/reports`)

### TC-ADM-089 — Generate an Attendance report for a valid date range

- **Portal / Module:** Admin → Reports
- **Priority:** P1
- **Mode:** API
- **Test Steps:**
  1. Select Attendance report type and a valid date range, Generate.
- **Expected Result:** Chart + table populate; totals match raw attendance data for that range.
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

### TC-ADM-090 — Invalid date range is blocked client-side

- **Portal / Module:** Admin → Reports
- **Priority:** P1
- **Mode:** API
- **Test Steps:**
  1. Select an end date before the start date, attempt Generate.
- **Expected Result:** Blocked with a validation message before any API call.
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

### TC-ADM-091 — Switching report types clears stale data

- **Portal / Module:** Admin → Reports
- **Priority:** P1
- **Mode:** API
- **Test Steps:**
  1. Generate a Revenue report.
  2. Switch to Performance, then Conversion, generating each in turn.
- **Expected Result:** Each renders type-appropriate chart/table with no leftover data from the previous type.
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

### TC-ADM-092 — Export CSV matches the on-screen report exactly

- **Portal / Module:** Admin → Reports
- **Priority:** P2
- **Mode:** API
- **Test Steps:**
  1. Generate any report, Export CSV.
- **Expected Result:** Downloaded file matches the on-screen table exactly.
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

### TC-ADM-093 — Report generation on a range with zero matching data

- **Portal / Module:** Admin → Reports
- **Priority:** P2
- **Mode:** API
- **Test Steps:**
  1. Generate a report for a date range with no activity (e.g. a future range).
- **Expected Result:** Clean empty state — no crash, no broken chart axes.
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

### 3.15 Bulk Email (`/admin/bulk-email`)

### TC-ADM-094 — Recipient count updates live per scope

- **Portal / Module:** Admin → Bulk Email
- **Priority:** P1
- **Mode:** API
- **Test Steps:**
  1. Switch scope between "All" and "Per Batch" (selecting different batches).
- **Expected Result:** Count updates via `GET bulk-email/recipients` and matches the actual active-parent count for each scope.
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

### TC-ADM-095 — Batch-scoped send reaches only that batch's parents

- **Portal / Module:** Admin → Bulk Email
- **Priority:** P1
- **Mode:** API
- **Test Steps:**
  1. Send a bulk email scoped to a single batch.
- **Expected Result:** `POST bulk-email` delivers only to parents of children in that batch — never platform-wide.
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

### TC-ADM-096 — Empty subject or body is blocked

- **Portal / Module:** Admin → Bulk Email
- **Priority:** P2
- **Mode:** API
- **Test Steps:**
  1. Attempt to send with an empty subject.
  2. Attempt to send with an empty body.
- **Expected Result:** Blocked client-side, or the server 400s — verify which actually happens for each case.
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

### TC-ADM-097 — "All" scope send reaches every active parent, no more, no less

- **Portal / Module:** Admin → Bulk Email
- **Priority:** P1
- **Mode:** API
- **Preconditions:** A dataset with active and inactive parent accounts.
- **Test Steps:**
  1. Send with scope = "All".
  2. Cross-check delivered recipients against the Users directory's active parents.
- **Expected Result:** Only active parents receive it; inactive/deactivated parent accounts are excluded.
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

### 3.16 Email Templates (`/admin/email-templates`)

### TC-ADM-098 — Edit template and verify placeholder substitution in preview

- **Portal / Module:** Admin → Email Templates
- **Priority:** P1
- **Mode:** API
- **Test Steps:**
  1. Edit a template's subject/HTML body, insert a placeholder token.
  2. Save.
  3. Open Preview tab.
- **Expected Result:** Persists; Preview renders the token substituted with sample data via `POST {id}/preview`.
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

### TC-ADM-099 — Toggling a template Inactive stops it from sending

- **Portal / Module:** Admin → Email Templates
- **Priority:** P1
- **Mode:** API
- **Test Steps:**
  1. Toggle a template's Active state off (e.g. booking confirmation).
  2. Trigger the corresponding real event (e.g. book a demo).
- **Expected Result:** Verify what "inactive" actually gates — confirm whether the corresponding system email stops sending, since this has real delivery implications and shouldn't be assumed.
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

### TC-ADM-100 — Screen degrades gracefully in demo mode

- **Portal / Module:** Admin → Email Templates
- **Priority:** P2
- **Mode:** Demo
- **Test Steps:**
  1. Load this screen with no `VITE_API_BASE_URL` configured.
- **Expected Result:** This is documented as an API-only screen with no demo fallback — verify it shows an empty/disabled state rather than crashing.
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

### TC-ADM-101 — Invalid HTML in template body is handled safely

- **Portal / Module:** Admin → Email Templates
- **Priority:** P2
- **Mode:** API
- **Test Steps:**
  1. Enter deliberately malformed HTML (unclosed tags) or a `<script>` tag into the body editor, save, preview.
- **Expected Result:** Saved content renders in Preview without executing any embedded script (no stored-XSS risk) and without breaking the surrounding page layout.
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

### 3.17 Progress Reports (`/admin/progress-reports`)

### TC-ADM-102 — Draft content saves and reloads correctly

- **Portal / Module:** Admin → Progress Reports
- **Priority:** P1
- **Mode:** API
- **Preconditions:** A draft auto-seeded for a child (1st-of-month background job).
- **Test Steps:**
  1. Write content, Save Draft.
  2. Navigate away and back.
- **Expected Result:** Persists as draft; editable again on return.
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

### TC-ADM-103 — Send locks the report against further edits

- **Portal / Module:** Admin → Progress Reports
- **Priority:** P0
- **Mode:** API
- **Test Steps:**
  1. Send a progress report to the parent, confirm.
  2. Attempt to edit it afterward.
- **Expected Result:** `POST {id}/send` succeeds; report locks against further edits; email delivered; report appears under Parent's "sent" reports only (`GET mine` excludes drafts).
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

### TC-ADM-104 — Month navigation handles a child not yet active in prior months

- **Portal / Module:** Admin → Progress Reports
- **Priority:** P2
- **Mode:** API
- **Preconditions:** A child who became active this month.
- **Test Steps:**
  1. Navigate to a month before the child was active.
- **Expected Result:** No draft exists for that month; clean empty state, no crash.
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

### TC-ADM-105 — Empty-content Send is blocked

- **Portal / Module:** Admin → Progress Reports
- **Priority:** P2
- **Mode:** API
- **Test Steps:**
  1. Attempt to Send a report whose draft content is empty.
- **Expected Result:** Blocked with a validation message — an empty report should not reach a parent.
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

### 3.18 Enrollments (`/admin/enrollments`)

### TC-ADM-106 — Approve creates the Child record and unlocks the parent dashboard

- **Portal / Module:** Admin → Enrollments
- **Priority:** P0
- **Mode:** API
- **Preconditions:** A submitted enrollment form pending review.
- **Test Steps:**
  1. Open the form, Approve (optionally picking a billing plan).
- **Expected Result:** `POST {id}/review`; creates the Child record; unlocks the Parent's dashboard; moves the form out of the pending queue.
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

### TC-ADM-107 — Edit answers pre-approval, blocked post-approval

- **Portal / Module:** Admin → Enrollments
- **Priority:** P1
- **Mode:** API
- **Test Steps:**
  1. Edit answers on a submitted-but-not-yet-approved form, save.
  2. Approve the form.
  3. Attempt to edit answers again.
- **Expected Result:** `PUT {id}` succeeds pre-approval; verify it's blocked (or irrelevant/read-only) post-approval.
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

### TC-ADM-108 — Download/Print a submission includes all required fields

- **Portal / Module:** Admin → Enrollments
- **Priority:** P1
- **Mode:** API
- **Test Steps:**
  1. Download/Print a submission.
- **Expected Result:** `GET {id}/download` returns the form as JSON/printable; every required field from `ENROLLMENT_FORM_FIELDS.md` is present and correctly labeled.
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

### TC-ADM-109 — Reject/decline an enrollment form

- **Portal / Module:** Admin → Enrollments
- **Priority:** P2
- **Mode:** API
- **Test Steps:**
  1. Reject/decline a submitted form (if the UI supports it distinct from Approve).
- **Expected Result:** Form marked appropriately; does not create a Child record; parent is notified of the outcome if that's part of the flow.
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

### TC-ADM-110 — Approving twice (double-click / race) does not create duplicate children

- **Portal / Module:** Admin → Enrollments
- **Priority:** P2
- **Mode:** API
- **Test Steps:**
  1. Rapidly double-click Approve on the same form (or fire two near-simultaneous `POST {id}/review` calls).
- **Expected Result:** Only one Child record is created; the second call either no-ops or returns a conflict, never a duplicate child.
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

### 3.19 Store Inquiries (`/admin/store-inquiries`)

### TC-ADM-111 — Public inquiry appears correctly in the staff queue

- **Portal / Module:** Admin → Store Inquiries
- **Priority:** P1
- **Mode:** API
- **Preconditions:** A public "Enroll Now" inquiry submitted via `/store` (TC-MKT-002).
- **Test Steps:**
  1. Open Store Inquiries.
- **Expected Result:** The lead is visible with correct contact/child details matching what was submitted.
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

### TC-ADM-112 — Status transitions persist correctly

- **Portal / Module:** Admin → Store Inquiries
- **Priority:** P1
- **Mode:** API
- **Test Steps:**
  1. Change an inquiry's status New → Contacted → Converted → Closed, one step at a time.
- **Expected Result:** `PUT {id}/status` persists each transition correctly; the current status always matches the last change made.
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

### TC-ADM-113 — Status can be set directly without passing through intermediate states

- **Portal / Module:** Admin → Store Inquiries
- **Priority:** P2
- **Mode:** API
- **Test Steps:**
  1. Set a New inquiry's status directly to Closed, skipping Contacted/Converted.
- **Expected Result:** Verify actual behavior — either allowed (no enforced state machine) or blocked with a clear message; document whichever is true rather than assuming.
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

### 3.20 Settings (`/admin/settings`)

### TC-ADM-114 — General org fields persist and reach the public settings endpoint

- **Portal / Module:** Admin → Settings → General
- **Priority:** P1
- **Mode:** API
- **Test Steps:**
  1. Edit org name/contact info, Save.
  2. Call `GET /api/settings/public` (unauthenticated).
- **Expected Result:** Changes persist via `PUT /api/settings`; the public endpoint used by the unauthenticated login screen reflects them.
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

### TC-ADM-115 — Branding changes apply platform-wide after reload

- **Portal / Module:** Admin → Settings → Branding
- **Priority:** P1
- **Mode:** API
- **Test Steps:**
  1. Edit logo/primary color, check live preview.
  2. Save, reload the app (including `/login`).
- **Expected Result:** Preview updates immediately on the settings screen; saved values apply platform-wide (login screen, sidebar) after reload.
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

### TC-ADM-116 — Fee payment reminders toggle actually gates the job

- **Portal / Module:** Admin → Settings → Notifications
- **Priority:** P1
- **Mode:** API
- **Test Steps:**
  1. Toggle "Fee payment reminders" off.
  2. Simulate/wait for the billing background job's 08:00 UTC reminder pass with an eligible invoice present.
- **Expected Result:** No reminder emails sent while toggled off (cross-ref TC-JOB-004/005).
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

### TC-ADM-117 — Weekly summary digest toggle actually gates the job

- **Portal / Module:** Admin → Settings → Notifications
- **Priority:** P1
- **Mode:** API
- **Test Steps:**
  1. Toggle "Weekly summary digest" off.
  2. Simulate/wait for Monday 07:00 UTC.
- **Expected Result:** `ReportsDigestBackgroundService`'s run sends nothing while toggled off (cross-ref TC-JOB-011/012).
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

### TC-ADM-118 — New menu item appears in the correct role's sidebar

- **Portal / Module:** Admin → Settings → Menus
- **Priority:** P1
- **Mode:** API
- **Test Steps:**
  1. Create a new sidebar menu item scoped to a specific role, optionally requiring a permission.
  2. Log in as that role (with and without the required permission, if applicable).
- **Expected Result:** Item appears in that role's sidebar (`GET /api/menus/mine` resolves it) when the permission requirement is met, and is hidden when it isn't.
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

### TC-ADM-119 — Payout rate card with a no-show penalty applies correctly

- **Portal / Module:** Admin → Settings → Payroll
- **Priority:** P1
- **Mode:** API
- **Test Steps:**
  1. Create/edit a payout rate card for a specific teacher/duration combo, including a no-show penalty value.
  2. Complete a session of that duration for that teacher.
  3. Mark a teacher no-show on another session of that duration for the same teacher.
- **Expected Result:** The completed session uses the configured rate; the no-show session applies the configured penalty (cross-ref TC-ADM-083).
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

### TC-ADM-120 — Integration secret fields mask input while typing

- **Portal / Module:** Admin → Settings → Integrations
- **Priority:** P0
- **Mode:** API
- **Test Steps:**
  1. Add/edit Razorpay credentials, typing directly into the secret-key field.
- **Expected Result:** Field renders as `type="password"` with a show/hide toggle while typing — was previously plaintext-on-entry; retest as a regression check (TC-GAP-004).
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

### TC-ADM-121 — Jitsi domain/autoRecord settings propagate to the classroom embed

- **Portal / Module:** Admin → Settings → Integrations
- **Priority:** P1
- **Mode:** API
- **Test Steps:**
  1. Set the Jitsi `domain` and `autoRecord` values, save.
  2. Join a live classroom session.
- **Expected Result:** The classroom embed picks up the configured domain; `autoRecord` behavior matches the configured value (cross-ref TC-CLS-020/021).
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

### TC-ADM-122 — Deleting an in-use integration is handled safely

- **Portal / Module:** Admin → Settings → Integrations
- **Priority:** P1
- **Mode:** API
- **Preconditions:** A payment account currently mapped to a specific integration (e.g. Razorpay).
- **Test Steps:**
  1. Delete that integration.
- **Expected Result:** Verify actual behavior — either blocked with a clear dependency message, or payments for the affected department fall back to the simulated gateway with the fallback **visibly flagged** (undesirable if silent — cross-ref TC-GAP-001).
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

### TC-ADM-123 — Deep link into a specific settings tab

- **Portal / Module:** Admin → Settings
- **Priority:** P2
- **Mode:** API
- **Test Steps:**
  1. Navigate to `/admin/settings?tab=integrations` (or another tab) directly.
- **Expected Result:** Lands directly on that tab, not the default General tab.
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

### TC-ADM-124 — Settings save is atomic across the bulk-upsert payload

- **Portal / Module:** Admin → Settings
- **Priority:** P2
- **Mode:** API
- **Test Steps:**
  1. Edit fields across two different tabs (e.g. General and Notifications) before a single Save (if the UI batches saves this way — verify first whether it does).
- **Expected Result:** Either all changes persist together, or each tab saves independently and clearly — no case where one tab's edit is silently lost because another tab's save overwrote the bulk payload.
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

---

## 4. Sub Admin Portal (`TC-SUB`)

5 screens under `/subadmin/*`. This role's entire identity is a customizable permission matrix
(see Section 2), so every screen here should be tested under at least two personas: one with
broad grants, one with a deliberately narrow preset, to confirm scoping actually restricts what's
shown.

### TC-SUB-001 — Dashboard reflects the RM's actual granted scope

- **Portal / Module:** Sub Admin → Dashboard
- **Priority:** P1
- **Mode:** API
- **Preconditions:** A Sub Admin with a broad preset.
- **Test Steps:**
  1. Log in, view Dashboard.
- **Expected Result:** Access-scope banner correctly names the granted modules; KPIs (batches/sessions/attendance/occupancy) reflect real data, scoped as intended, not platform-wide unless explicitly configured that way.
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

### TC-SUB-002 — Request additional access is a simulated flow, grants nothing

- **Portal / Module:** Sub Admin → Dashboard
- **Priority:** P2
- **Mode:** API
- **Test Steps:**
  1. Click "Request additional access", confirm.
- **Expected Result:** Simulated confirmation flow (documented as a simulated email, not a real request ticket); verify it doesn't silently grant any new permission.
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

### TC-SUB-003 — Permissions tab is a strictly read-only mirror

- **Portal / Module:** Sub Admin → Permissions
- **Priority:** P1
- **Mode:** API
- **Test Steps:**
  1. Open the Permissions tab.
  2. Attempt to interact with any matrix cell.
- **Expected Result:** Read-only matrix mirrors this RM's actual granted modules/actions exactly; no edit controls present; "Contact your Admin" button present but disabled/inert.
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

### TC-SUB-004 — Integrations tab works identically to Admin's when Settings:View is granted

- **Portal / Module:** Sub Admin → Integrations
- **Priority:** P0
- **Mode:** API
- **Preconditions:** RM preset includes `Settings:View` (and Edit, for full CRUD).
- **Test Steps:**
  1. Open Integrations, perform a full add/edit/delete cycle on a test integration entry.
- **Expected Result:** Renders the same `IntegrationsManager` component as Admin's screen; full CRUD works identically (re-run TC-ADM-120 through TC-ADM-122 here as a consistency check).
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

### TC-SUB-005 — Integrations tab is unreachable without Settings:View

- **Portal / Module:** Sub Admin → Integrations
- **Priority:** P0
- **Mode:** API
- **Preconditions:** RM preset without `Settings:View`.
- **Test Steps:**
  1. Navigate directly to `/subadmin/integrations` via URL.
- **Expected Result:** No-access empty state, not the CRUD form and not a raw exception.
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

### TC-SUB-006 — Reports tabs are gated independently per module

- **Portal / Module:** Sub Admin → Reports
- **Priority:** P1
- **Mode:** API
- **Preconditions:** RM with only some of the required modules granted.
- **Test Steps:**
  1. Open Reports, check each tab (Attendance / Batch Occupancy / Batch Roster).
- **Expected Result:** Only tabs backed by a granted module render real data; others show a permission-gated empty state (cross-ref TC-PERM-009).
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

### TC-SUB-007 — Export CSV respects the RM's scope, not platform-wide data

- **Portal / Module:** Sub Admin → Reports
- **Priority:** P2
- **Mode:** API
- **Test Steps:**
  1. Export CSV from a Reports tab the RM has access to.
- **Expected Result:** Downloads correctly; contents match the on-screen scoped data, not platform-wide figures.
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

### TC-SUB-008 — Audit Log is scoped to this RM's own actions

- **Portal / Module:** Sub Admin → Audit Log
- **Priority:** P1
- **Mode:** API
- **Test Steps:**
  1. Open Audit Log, filter by module.
- **Expected Result:** `GET /api/audit-logs` is scoped to this RM's own actions only, never another user's (cross-ref TC-PERM-012).
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

### TC-SUB-009 — Audit Log export communicates the 200-row cap

- **Portal / Module:** Sub Admin → Audit Log
- **Priority:** P2
- **Mode:** API
- **Preconditions:** More than 200 matching audit rows for this RM.
- **Test Steps:**
  1. Export Audit Log CSV.
- **Expected Result:** Export caps at 200 rows per the documented endpoint behavior; verify this is communicated in the UI (e.g. a note or truncation indicator), not a silent truncation.
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

### TC-SUB-010 — Schedule-conflicts panel surfaces conflicts for visibility

- **Portal / Module:** Sub Admin → Dashboard
- **Priority:** P2
- **Mode:** API
- **Preconditions:** A test-environment overlapping session created outside the normal double-booking guard (e.g. via direct data manipulation).
- **Test Steps:**
  1. View the "schedule conflicts" panel on the Dashboard.
- **Expected Result:** Panel surfaces the conflict for visibility even though the guard should normally prevent it at creation time.
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

### TC-SUB-011 — Two Sub Admins with different presets see different Dashboards side by side

- **Portal / Module:** Sub Admin → Dashboard
- **Priority:** P1
- **Mode:** API
- **Preconditions:** Two Sub Admin accounts with distinctly different presets.
- **Test Steps:**
  1. Log in as each in separate sessions, compare Dashboard contents.
- **Expected Result:** Each Dashboard reflects only its own RM's granted scope — no bleed-through of the other RM's modules or data.
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

---

## 5. Coordinator Portal (`TC-COR`)

3 screens under `/coordinator/*`. Coordinator's scope is session/calendar orchestration and leave
approval — no billing, no user management.

### TC-COR-001 — Dashboard data matches Admin's calendar for the same date

- **Portal / Module:** Coordinator → Dashboard
- **Priority:** P1
- **Mode:** API
- **Test Steps:**
  1. View Dashboard: today's sessions timeline, leave snapshot, KPIs.
  2. Cross-check against Admin's Sessions/Calendar for the same date.
- **Expected Result:** Data matches exactly — single source of truth, not a divergent copy.
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

### TC-COR-002 — Mark Holiday cancels the session and affects future scheduling

- **Portal / Module:** Coordinator → Calendar
- **Priority:** P0
- **Mode:** API
- **Test Steps:**
  1. Click a session on the calendar, choose "Mark Holiday", confirm.
  2. Run Generate Schedule for a batch whose window includes that date.
- **Expected Result:** Session is cancelled; the date becomes a holiday going forward and is skipped by future schedule generation (cross-ref TC-ADM-031).
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

### TC-COR-003 — Reschedule applies the documented +7-day default

- **Portal / Module:** Coordinator → Calendar
- **Priority:** P0
- **Mode:** API
- **Test Steps:**
  1. Click a session, choose "Reschedule", confirm.
- **Expected Result:** Session moves +7 days per the documented default offset — verify this exact behavior, not an arbitrary date picker, unless the UI genuinely offers manual date selection.
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

### TC-COR-004 — Join Class opens in monitor-only mode

- **Portal / Module:** Coordinator → Calendar
- **Priority:** P1
- **Mode:** API
- **Test Steps:**
  1. Click "Join Class" for a live session.
- **Expected Result:** Opens Jitsi in a new tab in monitor-only mode — Coordinator should not have moderator controls (mute-all, recording) that a Teacher has.
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

### TC-COR-005 — Approve a leave request within the valid window

- **Portal / Module:** Coordinator → Availability
- **Priority:** P0
- **Mode:** API
- **Preconditions:** A pending leave request submitted ≥6h before its session (TC-TCH-010).
- **Test Steps:**
  1. Approve the leave request.
- **Expected Result:** `POST /api/leave-requests/{id}/review`; teacher notified; session reflects the leave via a carried-forward reschedule per the no-show/leave rule.
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

### TC-COR-006 — Reject a leave request

- **Portal / Module:** Coordinator → Availability
- **Priority:** P1
- **Mode:** API
- **Test Steps:**
  1. Reject a pending leave request.
- **Expected Result:** Teacher notified of the rejection; the session remains scheduled as-is; no reschedule triggered.
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

### TC-COR-007 — Availability grid is consistent across desktop and mobile layouts

- **Portal / Module:** Coordinator → Availability
- **Priority:** P1
- **Mode:** API
- **Test Steps:**
  1. View the weekly teacher availability grid on a desktop viewport.
  2. Resize to a narrow/mobile viewport.
- **Expected Result:** Same underlying data in both; layout correctly switches from table to cards; no missing teachers in either view.
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

### TC-COR-008 — Per-teacher upcoming leave handles a zero-leave teacher

- **Portal / Module:** Coordinator → Availability
- **Priority:** P2
- **Mode:** API
- **Test Steps:**
  1. View the upcoming-leave list for a teacher with zero upcoming leave.
- **Expected Result:** Clean empty state, not a broken/undefined render.
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

### TC-COR-009 — Coordinator is blocked from billing/user-management actions

- **Portal / Module:** Coordinator (any screen) / direct API
- **Priority:** P1
- **Mode:** API
- **Test Steps:**
  1. Attempt any billing or user-management action via direct API call while authenticated as Coordinator.
- **Expected Result:** 403 across the board (cross-ref TC-PERM-016).
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

### TC-COR-010 — Approving leave for a session less than 6 hours away is still blocked at review time

- **Portal / Module:** Coordinator → Availability
- **Priority:** P1
- **Mode:** API
- **Preconditions:** A leave request that somehow reached the queue for a session now less than 6h away (e.g. approaching the boundary while pending).
- **Test Steps:**
  1. Attempt to approve it.
- **Expected Result:** Verify actual behavior — the 6-hour rule is enforced at *submission* time (TC-TCH-011); confirm whether approval is still allowed once the window has since closed, or whether the system re-checks at review time too.
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

---

## 6. Management Portal (`TC-MGT`)

4 screens under `/management/*`, deliberately **read-only by design** (spec-driven, not a gap —
see TC-PERM-005 and TC-GAP-006). Every case here should confirm data accuracy and the absence of
any mutation controls, not exercise CRUD.

### TC-MGT-001 — Dashboard KPIs reconcile with Admin's figures

- **Portal / Module:** Management → Dashboard
- **Priority:** P1
- **Mode:** API
- **Test Steps:**
  1. View Dashboard: executive KPIs + revenue trend/department pie/enrollment funnel charts.
  2. Cross-check the same period's figures on Admin → Dashboard.
- **Expected Result:** Figures reconcile exactly — no divergent calculation between the two "same KPI, different portal" views.
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

### TC-MGT-002 — Narrative summary text is consistent with the live numbers

- **Portal / Module:** Management → Dashboard
- **Priority:** P2
- **Mode:** API
- **Test Steps:**
  1. Read the narrative summary text on Dashboard, compare against the KPI figures shown alongside it.
- **Expected Result:** Text is generated/templated consistently with the underlying numbers — no stale hardcoded copy contradicting the live figures.
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

### TC-MGT-003 — Revenue table search/sort work with no edit affordance present

- **Portal / Module:** Management → Revenue
- **Priority:** P1
- **Mode:** API
- **Test Steps:**
  1. Search/sort the course-wise revenue table.
  2. Inspect the screen for any edit/write controls.
- **Expected Result:** Sort/search works client-side correctly; no edit affordance present anywhere on this screen.
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

### TC-MGT-004 — Teacher utilization figures match the documented KPI definition

- **Portal / Module:** Management → Performance
- **Priority:** P1
- **Mode:** API
- **Test Steps:**
  1. Review the teacher utilization meter and latest-payout column.
  2. Compare against `ANALYTICS_KPIS.md`'s definition and Admin → Payouts for the same teacher.
- **Expected Result:** Utilization = completed session hours ÷ available (leave-adjusted) hours; latest payout matches Admin → Payouts exactly.
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

### TC-MGT-005 — Board-pack CSV exports match their cards independently

- **Portal / Module:** Management → Reports
- **Priority:** P1
- **Mode:** API
- **Test Steps:**
  1. Export each of the 3 board-pack summary cards to CSV in turn.
- **Expected Result:** Each downloads independently with contents matching its own card, not another card's data.
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

### TC-MGT-006 — Write actions are blocked server-side, not just hidden client-side

- **Portal / Module:** Management (any screen) / direct API
- **Priority:** P0
- **Mode:** API
- **Test Steps:**
  1. Attempt any write action (edit course, confirm payment, approve leave) via direct API call while authenticated as Management.
- **Expected Result:** 403 on every one — confirms the read-only boundary is enforced server-side, not just hidden client-side.
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

### TC-MGT-007 — All 4 screens handle a zero-data deployment gracefully

- **Portal / Module:** Management (all screens)
- **Priority:** P2
- **Mode:** API
- **Preconditions:** A dataset with zero revenue/enrollments for the selected period (e.g. a brand-new deployment).
- **Test Steps:**
  1. Load all 4 Management screens.
- **Expected Result:** Charts render sensible empty/zero states, not NaN, broken axes, or crashes.
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

### TC-MGT-008 — Date-range/period filters, where present, scope every chart consistently

- **Portal / Module:** Management → Dashboard / Revenue / Performance
- **Priority:** P2
- **Mode:** API
- **Test Steps:**
  1. Change any period/date-range control present on these screens.
- **Expected Result:** All charts and tables on the screen update consistently to the new period — no chart left showing a stale range while others update.
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

---

## 7. Teacher Portal (`TC-TCH`)

7 screens under `/teacher/*`. Two flows here are hard business gates worth extra attention: the
**mandatory demo-feedback gate** and the **6-hour leave rule**.

### TC-TCH-001 — Dashboard shows only this teacher's own data

- **Portal / Module:** Teacher → Dashboard
- **Priority:** P1
- **Mode:** API
- **Preconditions:** Two teacher accounts, each with sessions scheduled today.
- **Test Steps:**
  1. Log in as Teacher A, view Dashboard.
  2. Log in as Teacher B, view Dashboard.
- **Expected Result:** Today's classes, week count, attendance average, and payout snapshot reflect each teacher's own data only — no cross-teacher bleed.
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

### TC-TCH-002 — Start Class launches successfully within the join window

- **Portal / Module:** Teacher → Dashboard / My Classes
- **Priority:** P0
- **Mode:** API
- **Preconditions:** A session scheduled to start within the next 10 minutes.
- **Test Steps:**
  1. Click "Start Class"/"Start Demo".
- **Expected Result:** Navigates to `/teacher/live/:sessionId`, launches successfully (see Section 12).
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

### TC-TCH-003 — Start Class is blocked outside the join window

- **Portal / Module:** Teacher → Dashboard / My Classes
- **Priority:** P1
- **Mode:** API
- **Test Steps:**
  1. Attempt to start a class more than 10 minutes before its scheduled start.
  2. Attempt to start a class after its scheduled duration has elapsed.
- **Expected Result:** Button disabled or absent in both cases, per the shared `isJoinable`/join-window rule.
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

### TC-TCH-004 — Pending demo-feedback banner links correctly

- **Portal / Module:** Teacher → Dashboard
- **Priority:** P1
- **Mode:** API
- **Preconditions:** One or more demo bookings pending feedback for this teacher.
- **Test Steps:**
  1. View Dashboard.
  2. Click the pending-demo-feedback banner.
- **Expected Result:** Banner appears and links correctly to `/teacher/demo-feedback`.
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

### TC-TCH-005 — Demo feedback is a hard gate, not optional

- **Portal / Module:** Teacher → My Classes / Demo Feedback
- **Priority:** P0
- **Mode:** API
- **Preconditions:** A demo session marked complete with no feedback submitted yet.
- **Test Steps:**
  1. Attempt to mark the demo fully "done"/move past it without submitting feedback.
- **Expected Result:** Feedback remains required — verify the workflow actually blocks progression (e.g. the booking stays flagged as needing feedback, downstream Admission review is blocked), not just a dismissible nag.
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

### TC-TCH-006 — Demo feedback requires "improvement areas"

- **Portal / Module:** Teacher → Demo Feedback
- **Priority:** P0
- **Mode:** API
- **Test Steps:**
  1. Open the feedback form, leave "improvement areas" empty, attempt Submit.
- **Expected Result:** Blocked client-side (required field) or 400 server-side; no partial feedback record persists.
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

### TC-TCH-007 — Complete feedback submission is visible identically to Admission

- **Portal / Module:** Teacher → Demo Feedback
- **Priority:** P1
- **Mode:** API
- **Test Data:** Academic level, strengths, improvement areas, recommended course, batch type, remarks — all filled.
- **Test Steps:**
  1. Submit complete demo feedback.
  2. Log in as Admission, view the same booking's feedback.
- **Expected Result:** `POST /api/demo-bookings/{id}/feedback` succeeds; appears under Submitted; Admission's read-only mirror shows identical content (cross-ref TC-ADS-007).
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

### TC-TCH-008 — Registered recording appears and respects the 15-day visibility rule

- **Portal / Module:** Teacher → My Classes
- **Priority:** P1
- **Mode:** API
- **Test Steps:**
  1. Register a recording URL manually for a completed session.
  2. Check the Recordings dialog.
  3. As the enrolled Parent, check visibility (cross-ref TC-PAR-015, TC-GAP-003).
- **Expected Result:** `POST {id}/recordings` succeeds; appears in the Recordings dialog; parent-facing visibility follows the documented 15-day rule.
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

### TC-TCH-009 — Attendance summary matches what was captured live

- **Portal / Module:** Teacher → Attendance
- **Priority:** P1
- **Mode:** API
- **Test Steps:**
  1. Open a completed session's attendance summary.
- **Expected Result:** Per-student present/absent matches what was captured during the live session (cross-ref TC-CLS-023), plus any session notes.
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

### TC-TCH-010 — Leave request accepted with ≥6 hours' notice

- **Portal / Module:** Teacher → Leave
- **Priority:** P0
- **Mode:** API
- **Preconditions:** A session starting 7+ hours from now.
- **Test Steps:**
  1. Request leave against that session, with a reason.
- **Expected Result:** Accepted, appears in leave history as Pending, routes to the Coordinator/relevant approver queue.
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

### TC-TCH-011 — Leave request hard-blocked under 6 hours' notice

- **Portal / Module:** Teacher → Leave
- **Priority:** P0
- **Mode:** API
- **Preconditions:** A session starting less than 6 hours from now.
- **Test Steps:**
  1. Attempt to request leave against that session.
  2. If bypassable client-side, attempt the equivalent direct API call.
- **Expected Result:** Hard-blocked client-side with a live "hours before" notice; server-side enforcement also rejects it if the client check is bypassed — this is documented as a strict rule, not a soft warning.
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

### TC-TCH-012 — Approved leave triggers a carried-forward replacement session

- **Portal / Module:** Teacher → Leave
- **Priority:** P1
- **Mode:** API
- **Preconditions:** A submitted leave request, approved by Coordinator/Admin (TC-COR-005).
- **Test Steps:**
  1. Check the affected session and the teacher's calendar after approval.
- **Expected Result:** Session reflects the approved leave; a carried-forward replacement session is created; teacher is notified.
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

### TC-TCH-013 — Leave request requires a reason

- **Portal / Module:** Teacher → Leave
- **Priority:** P2
- **Mode:** API
- **Test Steps:**
  1. Attempt to submit a leave request with the reason field empty.
- **Expected Result:** Blocked with a required-field message.
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

### TC-TCH-014 — Own Payout screen is strictly read-only

- **Portal / Module:** Teacher → Payout
- **Priority:** P1
- **Mode:** API
- **Test Steps:**
  1. View own Payout history.
  2. Inspect for any edit controls.
- **Expected Result:** Read-only; matches Admin's per-teacher payout records exactly; no edit controls present anywhere on this screen.
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

### TC-TCH-015 — Resource upload visibility respects selected batches only

- **Portal / Module:** Teacher → Resources
- **Priority:** P1
- **Mode:** API
- **Preconditions:** A teacher assigned to two or more batches.
- **Test Steps:**
  1. Upload a resource scoped to only one of the teacher's batches via the multi-batch visibility toggle.
  2. Check visibility from a parent in that batch vs. a parent in the teacher's other batch.
- **Expected Result:** Only visible/downloadable to parents/students in the selected batch(es), not the teacher's other unrelated batches.
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

### TC-TCH-016 — Ownership check blocks downloading another teacher's resource

- **Portal / Module:** Teacher → Resources / `GET {id}/mine/download`
- **Priority:** P2
- **Mode:** API
- **Preconditions:** A resource id belonging to a different teacher.
- **Test Steps:**
  1. Attempt to download that resource id via this teacher's session.
- **Expected Result:** 403 — ownership-checked, not just filtered out of the list view.
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

### TC-TCH-017 — Resource upload enforces the same size/empty-file rules as Admin

- **Portal / Module:** Teacher → Resources
- **Priority:** P1
- **Mode:** API
- **Test Steps:**
  1. Attempt to upload a file over 100MB.
  2. Attempt to upload a 0-byte file.
- **Expected Result:** Both rejected — the same caps enforced on Admin's upload path (TC-ADM-052/053) apply here too, not just on one of the two upload endpoints (cross-ref TC-SEC-005).
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

---

## 8. Parent Portal (`TC-PAR`)

7 screens under `/parent/*`. The **enrollment gate** (first-login mandatory form) and the
**Pay Now / fee-suspension** flow are the two highest-risk areas — money and access-blocking both
live here.

### TC-PAR-001 — Dashboard shows the enrollment gate for a not-yet-enrolled child

- **Portal / Module:** Parent → Dashboard
- **Priority:** P0
- **Mode:** API
- **Preconditions:** A parent whose child has been converted from a demo but has not yet submitted the enrollment form.
- **Test Steps:**
  1. Log in, view Dashboard.
- **Expected Result:** A "Complete Enrollment" CTA / pending-forms indicator is shown; content depending on an approved child (schedule, resources) reflects the gated state.
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

### TC-PAR-002 — Complete the 3-step enrollment wizard

- **Portal / Module:** Parent → Enrollment
- **Priority:** P0
- **Mode:** API
- **Test Steps:**
  1. Complete Student Details, Parent & Contact, and Course & Consent steps, including the required consent checkbox.
  2. Submit.
- **Expected Result:** `POST /api/enrollment-forms`; appears in Admin's Enrollments queue for review; Parent sees it as pending via `GET mine`.
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

### TC-PAR-003 — Wizard blocks advancing with a required field empty

- **Portal / Module:** Parent → Enrollment
- **Priority:** P1
- **Mode:** API
- **Test Steps:**
  1. On each step, leave one required field empty (e.g. child DOB on step 1, session-recording consent on step 3) and attempt Next/Submit.
- **Expected Result:** Blocked per-step, matching required fields in `ENROLLMENT_FORM_FIELDS.md`.
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

### TC-PAR-004 — Preferred-days multi-select persists as a set

- **Portal / Module:** Parent → Enrollment
- **Priority:** P1
- **Mode:** API
- **Test Steps:**
  1. Toggle multiple preferred-day checkboxes, submit.
  2. Review the submission (Admin → Enrollments detail).
- **Expected Result:** Persisted correctly as a set of days, not a single overwritten value.
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

### TC-PAR-005 — DOB field rejects invalid/future dates

- **Portal / Module:** Parent → Enrollment
- **Priority:** P2
- **Mode:** API
- **Test Steps:**
  1. Attempt to enter a future date of birth for the child.
- **Expected Result:** Blocked with a validation message.
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

### TC-PAR-006 — MultiChildSwitcher fully swaps scoped data

- **Portal / Module:** Parent → Dashboard
- **Priority:** P1
- **Mode:** API
- **Preconditions:** A parent with 2+ active children.
- **Test Steps:**
  1. Switch the active child via MultiChildSwitcher.
- **Expected Result:** All child-scoped data (KPIs, sessions, fee status) switches correctly and fully — no leftover data from the previously selected child.
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

### TC-PAR-007 — Pay Now via Cash creates a pending intent, not an immediate paid state

- **Portal / Module:** Parent → Dashboard / Billing
- **Priority:** P0
- **Mode:** API
- **Preconditions:** An overdue or pending invoice.
- **Test Steps:**
  1. Click Pay Now, choose Cash.
- **Expected Result:** Creates a Pending cash intent; invoice still shows as awaiting confirmation until Admin/Admission confirms it — not marked paid immediately.
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

### TC-PAR-008 — Pay Now via gateway routes to the parent's explicit choice first

- **Portal / Module:** Parent → Dashboard / Billing
- **Priority:** P0
- **Mode:** API
- **Test Steps:**
  1. Click Pay Now, choose a live gateway (Razorpay/Cashfree) explicitly.
- **Expected Result:** Routes to the gateway matching the parent's explicit choice first, correct department account second — regression case for the previously-broken routing (TC-GAP-001).
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

### TC-PAR-009 — In-page checkout settles via signature verification

- **Portal / Module:** Parent → Billing
- **Priority:** P1
- **Mode:** API
- **Test Steps:**
  1. Complete a gateway payment in test mode via the in-page checkout popup.
- **Expected Result:** `checkout/verify` validates the signature, settles the invoice, and the dashboard fee status updates without requiring a page reload.
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

### TC-PAR-010 — Refresh-payment catches a delayed/missed webhook

- **Portal / Module:** Parent → Billing
- **Priority:** P1
- **Mode:** API
- **Test Steps:**
  1. Pay via a gateway, then immediately trigger "refresh-payment" before any webhook fires.
- **Expected Result:** Polls the gateway directly and settles if actually paid — covers the case where a webhook is delayed or missed.
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

### TC-PAR-011 — Suspended account blocks Resources with a Pay Now message

- **Portal / Module:** Parent → Resources
- **Priority:** P0
- **Mode:** API
- **Preconditions:** An auto-suspended parent account (overdue invoice, no manual restore).
- **Test Steps:**
  1. Attempt to view Resources.
- **Expected Result:** 400 with a "Pay Now" message, not a silent empty list — matches `ParentPortalController.Resources`'s documented behavior.
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

### TC-PAR-012 — Suspended account's session-join behavior

- **Portal / Module:** Parent → Schedule
- **Priority:** P0
- **Mode:** API
- **Preconditions:** Same suspended account as TC-PAR-011.
- **Test Steps:**
  1. Attempt to view/join a scheduled Session.
- **Expected Result:** Verify whether join is actually blocked for suspended parents — flagged as "Sprint 2 enforcement" in the security plan; confirm current actual behavior rather than assuming it's live.
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

### TC-PAR-013 — Full payment auto-restores access without admin action

- **Portal / Module:** Parent → Billing / Resources
- **Priority:** P1
- **Mode:** API
- **Test Steps:**
  1. Pay the overdue invoice in full.
  2. Immediately re-check Resources access.
- **Expected Result:** Suspension auto-lifts; Resources/session access restored without needing an Admin action (cross-ref TC-ADM-087).
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

### TC-PAR-014 — Join Class button/tooltip reflects the join window accurately

- **Portal / Module:** Parent → Schedule
- **Priority:** P1
- **Mode:** API
- **Test Steps:**
  1. View a session currently within its join window.
  2. View one outside its join window.
- **Expected Result:** "Join Class" button state and tooltip text differ correctly per `isJoinable`/`joinHint` for each case.
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

### TC-PAR-015 — Personal calendar feed is correctly scoped and time-windowed

- **Portal / Module:** Parent → Schedule → Calendar tab
- **Priority:** P1
- **Mode:** API
- **Test Steps:**
  1. Get the personal `.ics` feed URL via CalendarSyncButton.
  2. Subscribe to it in an external calendar client.
  3. Cancel a session that was in the feed.
- **Expected Result:** Mints a long-lived personal token (`calendar/feed-url`); feed contains only this parent's/child's sessions, window −30/+120 days; cancellations show `STATUS:CANCELLED`.
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

### TC-PAR-016 — Recording visibility respects the 15-day window

- **Portal / Module:** Parent → Resources → Recordings
- **Priority:** P0
- **Mode:** API
- **Preconditions:** One recording registered less than 15 days ago, one older than 15 days.
- **Test Steps:**
  1. View the Recordings tab.
- **Expected Result:** Recent recording is playable; the one past 15 days disappears from the list — but also verify with TC-GAP-003 whether the underlying storage-deletion job has actually run (list-hiding vs. real deletion may diverge).
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

### TC-PAR-017 — Worksheet download is grant-checked

- **Portal / Module:** Parent → Resources
- **Priority:** P1
- **Mode:** API
- **Test Steps:**
  1. Download a worksheet the child's batch has been granted access to.
  2. Attempt to download a worksheet id **not** granted to this child (direct API call).
- **Expected Result:** First succeeds; second returns 403 (`resources/{id}/download` is grant-checked).
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

### TC-PAR-018 — Books are view-only, never downloadable

- **Portal / Module:** Parent → Resources
- **Priority:** P1
- **Mode:** API
- **Test Steps:**
  1. View a "book" type resource.
- **Expected Result:** View/preview works; no download affordance present anywhere on the screen for this resource type.
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

### TC-PAR-019 — Invoice download is ownership-checked

- **Portal / Module:** Parent → Billing
- **Priority:** P1
- **Mode:** API
- **Test Steps:**
  1. Download an invoice for one's own child.
  2. Attempt another parent's invoice id via direct API call.
- **Expected Result:** First succeeds; second returns 403/404 (ownership-checked).
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

### TC-PAR-020 — Notifications: mark-read and mark-all-read behave correctly

- **Portal / Module:** Parent → Notifications
- **Priority:** P1
- **Mode:** API
- **Test Steps:**
  1. Click one unread notification to mark it read.
  2. Click "Mark all read".
- **Expected Result:** Unread count updates correctly in both the feed and any topbar bell indicator after each action; day-grouping remains accurate throughout.
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

### TC-PAR-021 — Add Child roster matches Dashboard and routes into the real wizard

- **Portal / Module:** Parent → Add Child
- **Priority:** P2
- **Mode:** API
- **Test Steps:**
  1. View the roster of already-enrolled children plus any pending forms.
  2. Click "Add a child".
- **Expected Result:** Roster matches Dashboard's child list exactly; "Add a child" routes into the real Enrollment wizard rather than a separate demo-only shortcut (there is no quick-add form in API mode).
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

### TC-PAR-022 — Demo-mode quick-add is clearly non-persistent

- **Portal / Module:** Parent → Add Child
- **Priority:** P2
- **Mode:** Demo
- **Test Steps:**
  1. Use the quick-add form in demo mode.
  2. Reload the page.
- **Expected Result:** Succeeds visually but does not persist across reload — confirm it's clearly presented as a non-persistent demo affordance, not misleadingly implying it created a real record.
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

### TC-PAR-023 — Overdue-but-not-yet-suspended state shows a clear warning, not a hard block

- **Portal / Module:** Parent → Dashboard / Billing
- **Priority:** P1
- **Mode:** API
- **Preconditions:** An invoice recently gone overdue, before the billing job's suspension pass has run.
- **Test Steps:**
  1. View Dashboard/Billing.
- **Expected Result:** A clear overdue warning/fee-status badge is shown; access is not yet blocked until suspension actually triggers (cross-ref TC-JOB-003) — verify the two states (overdue-warned vs. suspended-blocked) are visually distinct.
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

---

## 9. Student Portal (`TC-STU`)

1 screen, reachable both as its own role and as a Parent's "preview my child's view" mode.

### TC-STU-001 — Dashboard reflects the active child's real data

- **Portal / Module:** Student → Dashboard
- **Priority:** P1
- **Mode:** API
- **Test Steps:**
  1. Log in as Student (or as Parent previewing a child), view Dashboard.
- **Expected Result:** Today's class, progress ring, attendance, stars, leaderboard, and badges all reflect the active child's real data (`getParentDashboard`/`getParentSchedule`/`getLeaderboard`/`getParentResources` under the hood).
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

### TC-STU-002 — Switching active child updates the preview

- **Portal / Module:** Student → Dashboard (Parent preview mode)
- **Priority:** P1
- **Mode:** API
- **Preconditions:** A parent with multiple children.
- **Test Steps:**
  1. Switch which child is "active".
  2. Reload `/student`.
- **Expected Result:** Preview reflects the newly active child, not a cached previous one.
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

### TC-STU-003 — Join Class gating matches Teacher/Parent's shared rule

- **Portal / Module:** Student → Dashboard
- **Priority:** P1
- **Mode:** API
- **Test Steps:**
  1. Check Join Class within the join window.
  2. Check it outside the join window.
- **Expected Result:** Same shared join-window gating as Teacher/Parent — button enabled/disabled consistently.
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

### TC-STU-004 — Leaderboard reflects newly earned stars

- **Portal / Module:** Student → Dashboard
- **Priority:** P2
- **Mode:** API
- **Preconditions:** Another student in the same session earns a star award (cross-ref TC-CLS-013/018).
- **Test Steps:**
  1. View the leaderboard widget.
- **Expected Result:** Leaderboard reflects the update — verify whether this requires a manual refresh or updates live.
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

### TC-STU-005 — Demo mode uses a stable fixed mock child

- **Portal / Module:** Student → Dashboard
- **Priority:** P2
- **Mode:** Demo
- **Test Steps:**
  1. Load `/student` in demo mode.
- **Expected Result:** Uses the fixed mock child `c-1` consistently — no crash from missing real child data.
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

### TC-STU-006 — Badges display accurately reflects earned milestones

- **Portal / Module:** Student → Dashboard
- **Priority:** P2
- **Mode:** API
- **Preconditions:** A child with at least one persisted `StudentAward`.
- **Test Steps:**
  1. View the badges section.
- **Expected Result:** Badges shown match the actual persisted award history for this child — no phantom badges, no missing ones.
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

---

## 10. Admission Portal (`TC-ADS`)

7 screens under `/admission/*`. This is the funnel: Demo Scheduling → Demo Feedback (read-only
mirror of Teacher's) → Leads → Conversion → Payments → Reports.

### TC-ADS-001 — Dashboard pipeline KPIs reconcile with real DemoBooking data

- **Portal / Module:** Admission → Dashboard
- **Priority:** P1
- **Mode:** API
- **Test Steps:**
  1. View Dashboard: pipeline KPIs, conversion funnel chart, today/upcoming demos.
- **Expected Result:** Numbers reconcile with the actual `DemoBooking` records (cross-check against Admin's reporting for the same period).
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

### TC-ADS-002 — Blank teacher field auto-assigns the least-loaded teacher

- **Portal / Module:** Admission → Demo Scheduling
- **Priority:** P0
- **Mode:** API
- **Preconditions:** At least two teachers with differing current workloads.
- **Test Steps:**
  1. Book a demo leaving the Teacher field blank.
- **Expected Result:** Auto-assigns the least-loaded teacher — verify it's actually load-based (fewest upcoming sessions), not just "first available" by id/name order.
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

### TC-ADS-003 — Multi-parent, multi-child demo booking creates all records correctly

- **Portal / Module:** Admission → Demo Scheduling
- **Priority:** P0
- **Mode:** API
- **Test Steps:**
  1. Book a demo with multiple parent rows and an extra invited child.
- **Expected Result:** `DemoBooking` + `DemoParticipant` records created correctly for all invitees; each receives appropriate confirmation.
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

### TC-ADS-004 — Demo booking rejects a double-booked teacher

- **Portal / Module:** Admission → Demo Scheduling
- **Priority:** P1
- **Mode:** API
- **Test Steps:**
  1. Book a demo at a date/time that conflicts with the assigned teacher's existing session.
- **Expected Result:** Rejected per the same double-booking rule enforced elsewhere (cross-ref TC-ADM-032/045).
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

### TC-ADS-005 — Marking a demo Completed before feedback is submitted

- **Portal / Module:** Admission → Demo Scheduling
- **Priority:** P1
- **Mode:** API
- **Preconditions:** A demo session that has occurred but has no feedback submitted yet.
- **Test Steps:**
  1. Attempt to mark the booking "Completed" from the Admission side.
- **Expected Result:** Verify actual behavior — does the UI allow this, or does it also respect the feedback gate documented on the Teacher side (TC-TCH-005)?
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

### TC-ADS-006 — Cancel a scheduled demo booking cleanly

- **Portal / Module:** Admission → Demo Scheduling
- **Priority:** P1
- **Mode:** API
- **Test Steps:**
  1. Cancel a scheduled demo booking, confirm.
- **Expected Result:** Removes/cancels the associated session; booking status updates; no orphaned session left joinable.
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

### TC-ADS-007 — Demo Feedback mirror matches Teacher's submission exactly

- **Portal / Module:** Admission → Demo Feedback
- **Priority:** P1
- **Mode:** API
- **Preconditions:** Feedback submitted by a Teacher (TC-TCH-007).
- **Test Steps:**
  1. View the same booking's feedback from Admission's Demo Feedback screen.
- **Expected Result:** Read-only mirror shows identical content; Admission cannot edit it here, only Teacher can via their own screen.
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

### TC-ADS-008 — Logging a follow-up updates the lead's stage and note log

- **Portal / Module:** Admission → Leads
- **Priority:** P0
- **Mode:** API
- **Test Steps:**
  1. Open a lead's detail dialog.
  2. Log a new follow-up note with a next-follow-up date and a move-to-stage selection.
- **Expected Result:** `PUT conversion-status`; note appended to the log; stage updates; "next follow-up" surfaces appropriately in the pipeline view.
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

### TC-ADS-009 — Demo history totals reconcile with real invoice/payment data

- **Portal / Module:** Admission → Leads
- **Priority:** P1
- **Mode:** API
- **Preconditions:** A parent with multiple past demo bookings.
- **Test Steps:**
  1. View the demo history panel's auto-calculated fee totals.
- **Expected Result:** Totals reconcile with actual invoice/payment data for that parent across all their demos.
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

### TC-ADS-010 — Cash confirmation queue is shared live with Admin's

- **Portal / Module:** Admission → Payments
- **Priority:** P0
- **Mode:** API
- **Test Steps:**
  1. Confirm a pending cash intent from this portal.
  2. Check the same intent from Admin → Billing.
- **Expected Result:** Same live queue as Admin → Billing — confirming here reflects immediately in Admin's view and vice versa (shared backend state, two UI surfaces).
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

### TC-ADS-011 — Generate & copy payment link produces a real, working link

- **Portal / Module:** Admission → Payments
- **Priority:** P0
- **Mode:** API
- **Test Steps:**
  1. Generate & copy a payment link for an invoice.
  2. Open the link and complete a test payment.
- **Expected Result:** `POST /api/invoices/{id}/payment-link` returns a real link tied to the correct department gateway; the payment actually completes — this replaced a previously-mock "Copy link" button, verify no leftover mock path remains (TC-GAP-005).
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

### TC-ADS-012 — Kanban stage transitions persist and move cards correctly

- **Portal / Module:** Admission → Conversion
- **Priority:** P1
- **Mode:** API
- **Test Steps:**
  1. Move a card through each stage via "Move to:": Demo Completed → Follow-up → Payment Pending → Partially Paid → Enrolled.
  2. Separately, move a different card to "Not Interested".
- **Expected Result:** Each transition persists (`PUT conversion-status`); the card moves to the correct column each time; "Not Interested" is reachable as an alternate terminal state.
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

### TC-ADS-013 — "Enrolled" payment-received subtitle is descriptive only (known gap)

- **Portal / Module:** Admission → Conversion
- **Priority:** P1
- **Mode:** API
- **Preconditions:** A lead in the "Enrolled" column with an actually-unpaid invoice.
- **Test Steps:**
  1. Check the "payment received" subtitle text against the real invoice/payment state.
- **Expected Result:** **Known gap**: this subtitle is descriptive text only, not tied to a real payment check — verify the lead can show "Enrolled — payment received" even though the invoice is still unpaid, and log this as a still-open item rather than a new discovery (cross-ref TC-GAP-007).
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

### TC-ADS-014 — Conversion-rate trend matches the documented KPI formula

- **Portal / Module:** Admission → Reports
- **Priority:** P1
- **Mode:** API
- **Test Steps:**
  1. Generate a conversion-rate trend and demos-per-teacher chart for a date range.
- **Expected Result:** Conversion rate matches `ANALYTICS_KPIS.md`'s definition: `DemoBooking(Enrolled) ÷ DemoBooking(DemoCompleted+)`.
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

### TC-ADS-015 — Report exports match on-screen filtered data

- **Portal / Module:** Admission → Reports
- **Priority:** P2
- **Mode:** API
- **Test Steps:**
  1. Export a feedback/payments/conversion report to CSV from this portal, with a filter applied.
- **Expected Result:** Matches on-screen data for the selected filters exactly.
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

### TC-ADS-016 — Duplicate lead detection on repeat demo booking

- **Portal / Module:** Admission → Demo Scheduling / Leads
- **Priority:** P2
- **Mode:** API
- **Preconditions:** A parent/child who already has an existing lead in the pipeline.
- **Test Steps:**
  1. Book another demo for the same parent/child.
- **Expected Result:** Verify actual behavior — either linked to the existing lead history (as seen in TC-ADS-009's demo-history panel) or creates a clearly distinguishable second entry; no silent merge that loses either booking's data.
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

---

## 11. Marketing Site & Public Store (`TC-MKT`)

Public, unauthenticated pages at `/` and `/store`.

### TC-MKT-001 — Public landing page loads with no session

- **Portal / Module:** Marketing → Home
- **Priority:** P2
- **Mode:** N/A
- **Test Steps:**
  1. Load `/` with no active session.
- **Expected Result:** Hero, feature grid, and 8-portal grid render; "Sign In" nav routes to `/login`; no forms are present, purely presentational.
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

### TC-MKT-002 — "Enroll Now" inquiry reaches Admin's queue

- **Portal / Module:** Store (`/store`)
- **Priority:** P0
- **Mode:** API
- **Test Steps:**
  1. Submit "Enroll Now" with valid parent+child details.
- **Expected Result:** `POST /api/store/inquiries`; appears in Admin → Store Inquiries (TC-ADM-111).
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

### TC-MKT-003 — "Book a Free Demo" creates a real DemoBooking

- **Portal / Module:** Store (`/store`)
- **Priority:** P0
- **Mode:** API
- **Test Steps:**
  1. Submit "Book a Free Demo" with a date/time between 2 hours and 29 days out.
- **Expected Result:** `POST /api/store/demo-bookings`; creates a `DemoBooking` with auto-assigned teacher, visible in Admission → Demo Scheduling.
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

### TC-MKT-004 — Demo booking window boundaries are enforced

- **Portal / Module:** Store (`/store`)
- **Priority:** P1
- **Mode:** API
- **Test Steps:**
  1. Attempt to book a demo less than 2 hours from now.
  2. Attempt to book a demo more than 29 days out.
  3. If bypassable client-side, attempt the equivalent direct API calls.
- **Expected Result:** Both blocked client-side per the documented min/max window; server-side enforcement also rejects them if the client check is bypassed.
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

### TC-MKT-005 — Store rate limit enforced across both inquiry endpoints

- **Portal / Module:** Store (`/store`)
- **Priority:** P1
- **Mode:** API
- **Test Steps:**
  1. Submit 6+ store inquiries/demo bookings from the same IP within 10 minutes (mix of both endpoint types).
- **Expected Result:** 429 after the 5th, consistent with the shared `store-inquiry` rate-limit policy across both endpoints.
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

### TC-MKT-006 — Public plan catalogue matches Admin's Packages data

- **Portal / Module:** Store (`/store`)
- **Priority:** P2
- **Mode:** API
- **Test Steps:**
  1. View plan cards on `/store`.
- **Expected Result:** `GET /api/store/plans` matches Admin → Packages plan data, publicly readable with no auth required (by design).
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

### TC-MKT-007 — Store forms validate required fields

- **Portal / Module:** Store (`/store`)
- **Priority:** P2
- **Mode:** API
- **Test Steps:**
  1. Attempt to submit "Enroll Now" or "Book a Free Demo" with parent name, email, or child name empty.
- **Expected Result:** Blocked client-side with clear validation messages; no request sent for incomplete data.
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

---

## 12. Live Classroom & Real-Time (`TC-CLS`)

Covers `JitsiLive.tsx`, `InteractivePanel.tsx`, `ClassroomHub` (SignalR), and the shared
`MockLiveClassroom` used in demo mode. This is the highest-complexity real-time surface in the
app — test with two real browser sessions (teacher + parent/student) wherever a case says
"two participants", not one tab pretending to be both.

### 12.1 Joining & room authorization

### TC-CLS-001 — Assigned teacher joins as moderator

- **Portal / Module:** Live Classroom / `ClassroomHub.JoinSession`
- **Priority:** P0
- **Mode:** API
- **Test Steps:**
  1. As the assigned Teacher, join a session within its window.
- **Expected Result:** `JoinSession` succeeds via `IsSessionParticipantAsync`; connects as moderator; Jitsi IFrame loads with the correct `MeetingRoomId` (`trn-…` format, never a manual/guessable link).
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

### TC-CLS-002 — Enrolled parent joins as non-moderator

- **Portal / Module:** Live Classroom / `ClassroomHub.JoinSession`
- **Priority:** P0
- **Mode:** API
- **Preconditions:** A parent whose child is enrolled in the session's batch.
- **Test Steps:**
  1. Join the session.
- **Expected Result:** Succeeds, connects as non-moderator.
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

### TC-CLS-003 — Non-enrolled parent is rejected at the authorization checkpoint

- **Portal / Module:** Live Classroom / `ClassroomHub.JoinSession`
- **Priority:** P0
- **Mode:** API
- **Preconditions:** A parent whose child is **not** enrolled in this session's batch.
- **Test Steps:**
  1. Attempt to join (e.g. by guessing/reusing a session id).
- **Expected Result:** `HubException` — non-participant, rejected at the single authorization checkpoint (`JoinSession`).
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

### TC-CLS-004 — Admin joins any session regardless of assignment

- **Portal / Module:** Live Classroom / `ClassroomHub.JoinSession`
- **Priority:** P0
- **Mode:** API
- **Test Steps:**
  1. As Admin, join any session.
- **Expected Result:** Succeeds regardless of assignment — Admin is an implicit participant per the service-level check.
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

### TC-CLS-005 — Invalid session id is rejected cleanly

- **Portal / Module:** Live Classroom / `ClassroomHub.JoinSession`
- **Priority:** P1
- **Mode:** API
- **Test Steps:**
  1. Call `JoinSession` with an invalid/non-existent session id.
- **Expected Result:** `HubException`, not a silent no-op or crash.
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

### TC-CLS-006 — Hub methods no-op before joining

- **Portal / Module:** Live Classroom / `ClassroomHub`
- **Priority:** P1
- **Mode:** API
- **Test Steps:**
  1. Call any hub method other than `JoinSession` (e.g. `SendChat`) without having joined first.
- **Expected Result:** Silent no-op per the documented `IsJoined` gate — no exception, no broadcast, cheap in-memory check.
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

### TC-CLS-007 — Board-access grants cannot cross session boundaries

- **Portal / Module:** Live Classroom / `ClassroomHub.SetBoardAccess`
- **Priority:** P1
- **Mode:** API
- **Preconditions:** Two teachers, two different sessions (Class A and Class B) open simultaneously.
- **Test Steps:**
  1. Teacher A attempts `SetBoardAccess` targeting a connection id that belongs to a participant in Class B.
- **Expected Result:** Rejected — the target connection must belong to the same room, preventing cross-session interference.
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

### TC-CLS-008 — Leave/disconnect cleans up room state correctly

- **Portal / Module:** Live Classroom / `ClassroomHub`
- **Priority:** P1
- **Mode:** API
- **Test Steps:**
  1. A participant leaves via `LeaveSession` (or closes the tab / drops connection).
- **Expected Result:** Removed from the group; roster rebroadcast to remaining participants; if they were the last one in the room, in-memory `Scores` for that session clear (durable `StudentAward` history is untouched).
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

### TC-CLS-009 — Rejoin reseeds leaderboard from persisted awards

- **Portal / Module:** Live Classroom / `ClassroomHub.JoinSession`
- **Priority:** P2
- **Mode:** API
- **Test Steps:**
  1. Rejoin a session after leaving, once the room is empty.
- **Expected Result:** Roster and leaderboard reseed correctly from persisted `StudentAward` data on first join of a fresh (now-empty) room.
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

### 12.2 In-classroom interaction

### TC-CLS-010 — Whiteboard ops relay in near-real-time

- **Portal / Module:** Live Classroom / `ClassroomHub.SendBoard`
- **Priority:** P0
- **Mode:** API
- **Test Steps:**
  1. Teacher draws on the whiteboard.
  2. Parent/Student in the same session observes.
- **Expected Result:** `SendBoard` relays the op to all others in the room in near-real-time.
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

### TC-CLS-011 — Chat broadcasts to the whole group with correct display names

- **Portal / Module:** Live Classroom / `ClassroomHub.SendChat`
- **Priority:** P1
- **Mode:** API
- **Test Steps:**
  1. Any joined participant sends a chat message.
- **Expected Result:** `SendChat` broadcasts with correctly resolved display name to the whole group, including the sender.
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

### TC-CLS-012 — Empty chat text is rejected

- **Portal / Module:** Live Classroom / `ClassroomHub.SendChat`
- **Priority:** P2
- **Mode:** API
- **Test Steps:**
  1. A non-teacher participant attempts `SendChat` with empty text.
- **Expected Result:** Rejected/no-op per the non-empty-text requirement.
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

### TC-CLS-013 — Full quiz cycle updates scores and leaderboard correctly

- **Portal / Module:** Live Classroom / `ClassroomHub` quiz methods
- **Priority:** P0
- **Mode:** API
- **Test Steps:**
  1. Teacher calls `StartQuiz`.
  2. Students answer via `AnswerQuiz`.
  3. Teacher calls `EndQuiz`.
- **Expected Result:** `QuizStarted`/`QuizAnswer`/`QuizEnded` broadcast correctly; correct answers increment the answering student's in-memory score by 1 star; leaderboard (top 10) rebroadcasts after each answer.
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

### TC-CLS-014 — Non-teacher cannot start or end a quiz

- **Portal / Module:** Live Classroom / `ClassroomHub.StartQuiz`/`EndQuiz`
- **Priority:** P1
- **Mode:** API
- **Test Steps:**
  1. A Parent/Student (non-teacher) attempts `StartQuiz` or `EndQuiz` directly.
- **Expected Result:** Rejected — gated by `IsTeacherInRoom`, which requires having joined and being recorded as teacher at join time.
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

### TC-CLS-015 — Celebrate triggers confetti for all participants

- **Portal / Module:** Live Classroom / `ClassroomHub.Celebrate`
- **Priority:** P2
- **Mode:** API
- **Test Steps:**
  1. Teacher calls `Celebrate` with a message.
- **Expected Result:** Broadcasts a celebration event; `GamificationOverlay` confetti triggers for all participants.
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

### TC-CLS-016 — Raise-hand state toggles and rebroadcasts the roster

- **Portal / Module:** Live Classroom / `ClassroomHub.RaiseHand`
- **Priority:** P1
- **Mode:** API
- **Test Steps:**
  1. A student calls `RaiseHand(true)`.
  2. Then `RaiseHand(false)`.
- **Expected Result:** Own hand-raised state toggles and roster rebroadcasts each time, visible to the teacher's Participants panel.
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

### TC-CLS-017 — Board-access grant/revoke targets only the intended student

- **Portal / Module:** Live Classroom / `ClassroomHub.SetBoardAccess`
- **Priority:** P1
- **Mode:** API
- **Test Steps:**
  1. Teacher grants whiteboard access to one specific student.
  2. Teacher revokes it.
- **Expected Result:** Only the targeted connection receives the `BoardAccess` toggle each time; that student's draw permission updates accordingly in the UI, unaffected students are untouched.
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

### TC-CLS-018 — Durable vs. ephemeral award mechanics are distinct

- **Portal / Module:** Live Classroom + `POST /api/gamification/awards`
- **Priority:** P1
- **Mode:** API
- **Test Steps:**
  1. A student earns a star via correct quiz answer (ephemeral, in-memory).
  2. A Teacher separately posts a persistent award via `POST /api/gamification/awards`.
  3. Close and reopen the room.
- **Expected Result:** The REST-posted award persists as `StudentAward` and survives room close; verify whether quiz-answer stars are also persisted somewhere or are purely ephemeral — document which is actually durable.
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

### TC-CLS-019 — Leaderboard is reachable outside the live room

- **Portal / Module:** `GET /api/gamification/leaderboard`
- **Priority:** P2
- **Mode:** API
- **Test Steps:**
  1. Call the leaderboard endpoint filtered by session, from outside the live room (e.g. Student dashboard post-class).
- **Expected Result:** Names-only leaderboard reflects persisted awards, reachable by any authenticated role, not just session participants.
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

### 12.3 Recording, engagement tracking, and Jitsi specifics

### TC-CLS-020 — Auto-record with no Jibri pool provisioned is a safe no-op

- **Portal / Module:** Live Classroom / Jitsi recording
- **Priority:** P0
- **Mode:** API
- **Preconditions:** Settings → Integrations → Jitsi `autoRecord=true`, no Jibri pool provisioned (typical for a fresh/dev deployment).
- **Test Steps:**
  1. Teacher joins.
- **Expected Result:** Per documented behavior, the auto-start command is a no-op — verify what actually happens on screen (silently nothing, vs. a confusing partial toolbar state) and that this doesn't block the rest of the class.
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

### TC-CLS-021 — Manual recording start registers correctly

- **Portal / Module:** Live Classroom / Jitsi recording
- **Priority:** P1
- **Mode:** API
- **Preconditions:** `autoRecord=false`.
- **Test Steps:**
  1. Teacher manually clicks the Jitsi toolbar/My-Classes "Recording" button.
- **Expected Result:** Recording starts (assuming Jibri is available) or fails gracefully; either path still auto-registers via `recordingLinkAvailable` once a recording exists.
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

### TC-CLS-022 — Engagement pings produce a coherent post-session summary

- **Portal / Module:** Live Classroom / engagement tracking
- **Priority:** P1
- **Mode:** API
- **Test Steps:**
  1. During a live session, allow engagement-tracking pings (talk-time, camera-on, attention) to fire.
  2. After the session, view `GET {id}/engagement` (staff-only).
- **Expected Result:** `POST {id}/engagement` calls fire periodically during the session; the summary is coherent and matches actual observed participation.
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

### TC-CLS-023 — Rejoin updates existing attendance rather than duplicating

- **Portal / Module:** Live Classroom / Attendance capture
- **Priority:** P1
- **Mode:** API
- **Test Steps:**
  1. A student joins a session, disconnects, then rejoins the same session.
  2. Teacher marks/reviews attendance after the session.
- **Expected Result:** Rejoin updates the existing attendance record rather than creating a duplicate row, per the documented join-based capture rule.
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

### TC-CLS-024 — Native Jitsi lobby/waiting room enforcement

- **Portal / Module:** Live Classroom / Jitsi lobby
- **Priority:** P2
- **Mode:** API
- **Test Steps:**
  1. Toggle the native Jitsi lobby/waiting room on.
  2. Have a participant attempt to join after the toggle.
- **Expected Result:** Prosody-level lobby enforced — the joining participant lands in a waiting state until admitted; verify current actual behavior, since JWT-secured rooms/lobby enforcement is flagged as "Sprint 2 production hardening" and may not be fully live yet (cross-ref TC-GAP-008).
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

### TC-CLS-025 — Moderator controls: verify which are actually functional

- **Portal / Module:** Live Classroom / Jitsi moderator controls
- **Priority:** P1
- **Mode:** API
- **Test Steps:**
  1. Teacher uses in-Jitsi moderator controls: mute participant, disable camera, `muteEveryone`.
- **Expected Result:** These map to IFrame API commands per the architecture doc; some are flagged "In Progress" in the backlog — verify which specific controls are actually functional right now vs. visually present but inert, and record each individually.
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

### TC-CLS-026 — Extended-duration session stability

- **Portal / Module:** Live Classroom / Jitsi
- **Priority:** P2
- **Mode:** API
- **Test Steps:**
  1. Run a session past 60 minutes continuously.
- **Expected Result:** No hard cutoff from the app or Jitsi (self-hosted, unlike public `meet.jit.si`'s 5-min cap) — verify stability is bounded only by client resources, not an artificial timeout; full concurrency/load behavior is explicitly out of scope until Sprint 5 load testing, so treat failures here as a scheduling/capacity note, not necessarily a functional bug.
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

### TC-CLS-027 — Full mock-classroom flow works end-to-end in demo mode

- **Portal / Module:** Live Classroom (Demo)
- **Priority:** P1
- **Mode:** Demo
- **Test Steps:**
  1. Run the full mock classroom flow: mic/cam/screen-share toggle, whiteboard, waiting-room admit/deny, quiz launch, gamification celebration.
- **Expected Result:** All interactions work against local mock state with no backend; the experience is clearly presented as simulated (no real video) and does not imply real persistence.
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

### TC-CLS-028 — Direct navigation to a live-class URL without router state falls back gracefully

- **Portal / Module:** Live Classroom
- **Priority:** P1
- **Mode:** API
- **Test Steps:**
  1. Navigate directly to `/parent/live/:sessionId` or `/teacher/live/:sessionId` by typing the URL (not via the "Start Class" button, so no router `state` is present).
- **Expected Result:** Falls back to the documented "can't be opened directly" screen rather than crashing or exposing a broken embed.
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

### TC-CLS-029 — Camera/microphone permission denial is handled gracefully

- **Portal / Module:** Live Classroom
- **Priority:** P2
- **Mode:** API
- **Test Steps:**
  1. Join a live session in a browser where camera/microphone permission is denied at the OS/browser level.
- **Expected Result:** Session still loads with audio/video disabled and a clear indicator, not a crash or infinite loading state.
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

### TC-CLS-030 — Whiteboard multi-page and undo/clear work correctly

- **Portal / Module:** Live Classroom / Whiteboard
- **Priority:** P2
- **Mode:** Both
- **Test Steps:**
  1. Draw on the whiteboard, add a new page, switch between pages.
  2. Use Clear on one page, then Undo.
- **Expected Result:** Each page retains its own independent content; Clear removes the current page's content; Undo restores it; other pages are unaffected throughout.
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

---

## 13. Billing & Payments Deep-Dive (`TC-BIL`)

Portal screens already exercise the everyday CRUD (Section 3.9–3.11, Section 8, Section 10). This
section is for the parts that don't map to a single screen: webhooks, gateway routing edge cases,
and the invoice lifecycle machine. This is the single highest financial-risk area in the app —
prioritize it if time is constrained.

### TC-BIL-001 — Valid Razorpay webhook settles the transaction

- **Portal / Module:** `POST /api/payments/webhook/razorpay`
- **Priority:** P0
- **Mode:** API
- **Test Steps:**
  1. Send a valid `payment_link.paid` webhook POST with a correct HMAC-SHA256 signature in `X-Razorpay-Signature`.
- **Expected Result:** 200; matching transaction settles; invoice updates; receipt/notification flow fires.
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

### TC-BIL-002 — Invalid webhook signature is rejected before any state change

- **Portal / Module:** `POST /api/payments/webhook/razorpay`
- **Priority:** P0
- **Mode:** API
- **Test Steps:**
  1. Send the same webhook payload with a tampered/invalid signature.
- **Expected Result:** 401; no state change occurs.
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

### TC-BIL-003 — Missing webhook secret fails closed

- **Portal / Module:** `POST /api/payments/webhook/razorpay`
- **Priority:** P0
- **Mode:** API
- **Preconditions:** No webhook secret configured in Settings/Integrations.
- **Test Steps:**
  1. Send a webhook POST.
- **Expected Result:** 401 — fails closed, not open (no secret ≠ accept-anything).
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

### TC-BIL-004 — Unknown gateway reference is idempotently ignored

- **Portal / Module:** `POST /api/payments/webhook/razorpay`
- **Priority:** P1
- **Mode:** API
- **Test Steps:**
  1. Send a webhook referencing a gateway payment reference that doesn't match any known transaction.
- **Expected Result:** Caught as `NotFoundException` but still returns 200 — intentional idempotency (prevents Razorpay's retry storm), not a data leak; verify no state changes occur and no exception surfaces to the client.
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

### TC-BIL-005 — Expired/cancelled payment-link events settle as failed

- **Portal / Module:** `POST /api/payments/webhook/razorpay`
- **Priority:** P1
- **Mode:** API
- **Test Steps:**
  1. Send a `payment_link.expired` event.
  2. Send a `payment_link.cancelled` event.
- **Expected Result:** Both settle the transaction as failed, not paid; invoice remains unpaid/overdue as appropriate.
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

### TC-BIL-006 — Unrecognized webhook event type is handled forward-compatibly

- **Portal / Module:** `POST /api/payments/webhook/razorpay`
- **Priority:** P2
- **Mode:** API
- **Test Steps:**
  1. Send an event type Razorpay doesn't currently document (simulate a future/unknown event).
- **Expected Result:** Logged, 200 returned, no crash.
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

### TC-BIL-007 — Cashfree webhook mirrors the same rigor as Razorpay

- **Portal / Module:** `POST /api/payments/webhook/cashfree`
- **Priority:** P0
- **Mode:** API
- **Test Steps:**
  1. Repeat TC-BIL-001 through TC-BIL-006 against this endpoint (`x-webhook-signature` + `x-webhook-timestamp`, base64 HMAC, `PAYMENT_LINK_EVENT` PAID/EXPIRED/CANCELLED).
- **Expected Result:** Same pass/fail pattern as Razorpay for every case — the two gateways must be equally rigorous, not just the one the team tested more.
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

### TC-BIL-008 — Cashfree refund against a settled-transaction-id-only record

- **Portal / Module:** Admin → Billing → Refunds / Cashfree
- **Priority:** P1
- **Mode:** API
- **Preconditions:** An invoice paid via Cashfree, real test credentials configured.
- **Test Steps:**
  1. Trigger a Cashfree refund (TC-ADM-063) where only a settled transaction id is captured, not a separate order id.
- **Expected Result:** This is flagged as an integration risk needing verification against Cashfree's current API — confirm whether the refund call actually succeeds against real Cashfree test credentials, or document the failure mode precisely if it doesn't.
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

### TC-BIL-009 — Cancelled invoice display state (known open gap)

- **Portal / Module:** Parent → Billing / `INVOICE_STATUS_FROM_API`
- **Priority:** P0
- **Mode:** API
- **Test Steps:**
  1. Create an invoice, cancel it (if reachable via the API).
  2. View it on the Parent Billing screen.
- **Expected Result:** **Known open gap**: `Invoice.Cancelled` is currently silently mapped to `"pending"` on the frontend — verify the cancelled invoice incorrectly displays as still-awaiting-payment; this is a confirmed defect, not a new discovery (cross-ref TC-GAP-009). Do not let a parent be prompted to pay a cancelled invoice in a real deployment.
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

### TC-BIL-010 — Simulated-gateway fallback is now visibly surfaced

- **Portal / Module:** Admin → Payment Mapping / Parent → Pay Now
- **Priority:** P1
- **Mode:** API
- **Preconditions:** A department's `PaymentAccount.GatewayProvider` set to a placeholder/non-matching string (e.g. leftover `"pending-client-decision"`).
- **Test Steps:**
  1. Have a parent pay via an admin-generated share link (no explicit method carried).
- **Expected Result:** Falls back to `SimulatedPaymentGateway` (`SIM-…` link) — verify this fallback is now clearly surfaced to the user/admin, not silent, per the fix described in TC-GAP-001.
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

### TC-BIL-011 — Manual payment dedupes against a pending cash intent (regression)

- **Portal / Module:** Admin → Billing / `RecordPaymentAsync`
- **Priority:** P1
- **Mode:** API
- **Test Steps:**
  1. Parent declares cash payment (`methodKey: "cash"`).
  2. Admin separately records a manual payment for the same invoice before the cash intent is confirmed/rejected.
- **Expected Result:** The manual entry settles the existing Pending cash-intent row instead of creating a second, orphaned Pending row — regression-test this dedup directly against the transaction list/API response, not just the UI total.
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

### TC-BIL-012 — Confirm/Reject require Approve specifically, not just Edit

- **Portal / Module:** Admission → Payments / `BillingFinance:Approve`
- **Priority:** P1
- **Mode:** API
- **Preconditions:** An Admission Team user with only `BillingFinance: View, Edit` granted (no Approve) — e.g. a custom preset that deviates from the seeded default.
- **Test Steps:**
  1. Attempt to confirm a pending cash intent.
- **Expected Result:** 403 — the queue itself is still visible (read-only, lock icon), but action buttons don't render and the endpoint rejects even if called directly.
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

### TC-BIL-013 — Cash confirmation notifies both Admin and Admission Team

- **Portal / Module:** Admin/Admission → Billing/Payments
- **Priority:** P1
- **Mode:** API
- **Test Steps:**
  1. Confirm a cash intent.
  2. Check notification recipients.
- **Expected Result:** Both Admin and Admission Team roles are notified (documented as a fix — was Admin-only before).
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

### TC-BIL-014 — Cash-path full payment also auto-lifts suspension

- **Portal / Module:** Admin → Billing / Fee Suspension
- **Priority:** P1
- **Mode:** API
- **Test Steps:**
  1. Fully pay an overdue invoice via cash confirmation (not gateway).
- **Expected Result:** Fee suspension auto-lifts on full payment through this path too, not just the gateway path (cross-ref TC-PAR-013).
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

### TC-BIL-015 — Gateway credential fields are masked on both read and write

- **Portal / Module:** Admin → Settings → Integrations
- **Priority:** P2
- **Mode:** API
- **Test Steps:**
  1. Save Razorpay/Cashfree credentials.
  2. Reload the page and view the fields.
  3. Edit the fields and observe them while typing.
- **Expected Result:** Values are masked on read and masked while typing (both were previously inconsistent — verify both states now hold).
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

### TC-BIL-016 — Invoice childName/courseName resolve correctly, not to a placeholder

- **Portal / Module:** Admin → Billing / Invoice detail
- **Priority:** P1
- **Mode:** API
- **Test Steps:**
  1. Create an invoice for a real child/course combination.
  2. View the resulting Invoice detail and PDF.
  3. Separately, create a manual charge with no course linkage.
- **Expected Result:** The linked invoice correctly resolves `childName`/`courseName`, not the old hardcoded `"—"` placeholder; the genuinely unlinked manual charge correctly falls back to `"—"`.
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

### TC-BIL-017 — Currency/decimal precision is consistent end-to-end

- **Portal / Module:** Billing (all screens)
- **Priority:** P1
- **Mode:** API
- **Test Steps:**
  1. Create an invoice with a price that has 2 decimal places (e.g. 4999.99).
  2. Follow it through payment, receipt, and payout-adjacent calculations if applicable.
- **Expected Result:** No rounding drift across the pipeline — the amount displayed on the invoice, the receipt, and the gateway checkout page all agree to the cent/paisa.
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

### TC-BIL-018 — Concurrent payment attempts on the same invoice don't double-charge

- **Portal / Module:** Parent → Billing / Pay Now
- **Priority:** P1
- **Mode:** API
- **Test Steps:**
  1. Open Pay Now in two browser tabs for the same invoice.
  2. Complete payment in both nearly simultaneously.
- **Expected Result:** Only one payment settles against the invoice; the second either fails cleanly (invoice already paid) or is refused before reaching the gateway — no double-charge and no double-settled invoice state.
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

---

## 14. Security Test Cases (`TC-SEC`)

Cross-references `SECURITY_TEST_PLAN.md`'s defined scope. Sections 1–2 already cover most AuthN/AuthZ
abuse cases in portal-realistic form — this section adds the lower-level/infrastructure checks.

### TC-SEC-001 — SQL-injection-style payloads are neutralized

- **Portal / Module:** Any search/filter field (e.g. Users search, Reports filters)
- **Priority:** P0
- **Mode:** API
- **Test Data:** `' OR '1'='1`, `'; DROP TABLE users; --`, and similar payloads.
- **Test Steps:**
  1. Submit each payload into a search/filter text field.
- **Expected Result:** No error, no unexpected data exposure — EF Core parameterizes all queries by design; treat any raw-SQL-shaped bypass as a P0 finding.
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

### TC-SEC-002 — Malformed/oversized JSON payloads are rejected cleanly

- **Portal / Module:** Any POST/PUT endpoint
- **Priority:** P1
- **Mode:** API
- **Test Steps:**
  1. Submit malformed JSON (broken syntax) to a write endpoint.
  2. Submit a deeply nested or excessively large JSON payload.
- **Expected Result:** 400 via DTO validation in both cases, not a 500 or an unhandled exception leaking a stack trace.
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

### TC-SEC-003 — Unexpected server errors never leak internals

- **Portal / Module:** Any endpoint
- **Priority:** P1
- **Mode:** API
- **Test Steps:**
  1. Trigger a deliberate server error (e.g. malformed-but-plausible input that reaches an edge-case code path).
- **Expected Result:** Generic 500 "An unexpected error occurred." returned to the client — verify no stack trace, connection string, or internal file path is ever leaked in the response body.
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

### TC-SEC-004 — Path-traversal filenames are neutralized on every upload path

- **Portal / Module:** Resources upload (Admin and Teacher)
- **Priority:** P1
- **Mode:** API
- **Test Steps:**
  1. Upload a file via each upload endpoint using a crafted path-traversal filename.
- **Expected Result:** Stored under a GUID name server-side on both paths — filename is never used directly as a path component (cross-ref TC-ADM-056).
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

### TC-SEC-005 — 100MB upload cap is enforced on every upload endpoint

- **Portal / Module:** Resources upload (Admin and Teacher)
- **Priority:** P0
- **Mode:** API
- **Test Steps:**
  1. Upload a file just over 100MB via Admin's Resources screen.
  2. Repeat via Teacher's Resources screen.
- **Expected Result:** Rejected consistently on both — verify the cap isn't only enforced on one of the two upload paths.
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

### TC-SEC-006 — No gateway secrets ever appear in API responses

- **Portal / Module:** `GET /api/payment-accounts`, invoice detail
- **Priority:** P0
- **Mode:** API
- **Test Steps:**
  1. Inspect the raw response body of any endpoint returning payment account data.
- **Expected Result:** No gateway secret keys/tokens present — only external references (account ids, provider names) per the documented "no gateway secrets in the database... or in API responses" rule.
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

### TC-SEC-007 — Plain HTTP is redirected to HTTPS

- **Portal / Module:** Deployed (non-dev) environment
- **Priority:** P1
- **Mode:** API
- **Test Steps:**
  1. Attempt to load the app over plain HTTP.
- **Expected Result:** Redirected to HTTPS.
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

### TC-SEC-008 — CORS blocks disallowed origins from a real browser context

- **Portal / Module:** API CORS policy
- **Priority:** P1
- **Mode:** API
- **Test Steps:**
  1. From a browser page served on a non-allow-listed origin, attempt a cross-origin fetch to the API.
- **Expected Result:** Blocked by CORS — verify from an actual cross-origin browser context, not just a curl request (curl ignores CORS).
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

### TC-SEC-009 — Wildcard CORS origin fails startup

- **Portal / Module:** API startup configuration
- **Priority:** P2
- **Mode:** API
- **Test Steps:**
  1. Attempt to start the API with `Cors:AllowedOrigins` containing `"*"`.
- **Expected Result:** Startup throws immediately — fail-fast, confirms this misconfiguration can't accidentally reach production.
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

### TC-SEC-010 — Weak/missing JWT signing key fails startup

- **Portal / Module:** API startup configuration
- **Priority:** P2
- **Mode:** API
- **Test Steps:**
  1. Attempt to start the API with a JWT signing key under 32 bytes or missing entirely.
- **Expected Result:** Startup throws immediately, same fail-fast pattern.
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

### TC-SEC-011 — Temp passwords are delivered in plain text (documented, accepted risk)

- **Portal / Module:** Admin → Users → credential delivery
- **Priority:** P1
- **Mode:** API
- **Test Steps:**
  1. Inspect a credential-delivery email/WhatsApp message sent on user creation.
- **Expected Result:** Temp password is plain text in the message body — this is a documented, accepted risk (forced first-login password change is separately tracked), not something to file as a new finding; verify it matches the documented state rather than assuming it's fixed.
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

### TC-SEC-012 — Expired token is handled cleanly with no refresh-token rotation

- **Portal / Module:** Auth / token lifecycle
- **Priority:** P2
- **Mode:** API
- **Test Steps:**
  1. Attempt to use an access token after its 8-hour lifetime elapses (or a shortened test-config expiry).
- **Expected Result:** 401 — no refresh-token rotation exists yet (documented, accepted-for-now risk); confirm the user is cleanly redirected to `/login` rather than stuck in a broken state.
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

### TC-SEC-013 — Dependency vulnerability baseline

- **Portal / Module:** Backend & frontend dependencies
- **Priority:** P0
- **Mode:** N/A
- **Test Steps:**
  1. Run `dotnet list package --vulnerable` in the backend.
  2. Run `npm audit` in the frontend.
- **Expected Result:** No unaddressed critical/high vulnerabilities in direct dependencies — part of the documented Sprint 5 security pass, worth running now as a baseline.
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

### TC-SEC-014 — Authorization is enforced server-side, not derived from client-controllable data

- **Portal / Module:** Any Admin-only endpoint
- **Priority:** P1
- **Mode:** API
- **Test Steps:**
  1. As a low-privilege role (e.g. Teacher), attempt to directly call an Admin-only endpoint using a captured/replayed request with the Teacher's own valid token (not a stolen admin token).
- **Expected Result:** 403 — confirms authorization is enforced server-side per-request, not derived from anything client-controllable in the request itself.
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

### TC-SEC-015 — Stored XSS is neutralized in user-generated text fields

- **Portal / Module:** Any free-text field rendered back to other users (chat, notes, email template body, session notes)
- **Priority:** P0
- **Mode:** API
- **Test Data:** `<script>alert(1)</script>`, `<img src=x onerror=alert(1)>`.
- **Test Steps:**
  1. Submit each payload into a free-text field that gets rendered back to another user (e.g. classroom chat, RM notes, email template body).
- **Expected Result:** Rendered as inert text, never executed as script, in every surface that displays the value.
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

### TC-SEC-016 — IDOR check across ownership-scoped resource ids

- **Portal / Module:** Parent invoices, resources, recordings; Teacher resources
- **Priority:** P0
- **Mode:** API
- **Test Steps:**
  1. As Parent A, capture a resource id belonging to Parent B's child (invoice, worksheet, recording).
  2. Attempt to fetch it directly via API using Parent A's own valid token.
- **Expected Result:** 403/404 for every resource type tested — no direct object reference lets one parent see another's data by id alone.
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

---

## 15. Background Jobs (`TC-JOB`)

Not user-triggered — these run on a timer inside the API host. Test by either waiting for the
real interval in a long-running environment, or by temporarily manipulating server clock/seed data
in a test environment to force the trigger condition. Each job's pattern (infinite loop + delay,
top-level try/catch, fresh DI scope per cycle) means a single bad row should never take down the
whole cycle — that isolation property is itself worth testing, not just the happy path.

### TC-JOB-001 — Billing cycle generates the next invoice on schedule

- **Portal / Module:** `BillingBackgroundService`
- **Priority:** P0
- **Mode:** API
- **Preconditions:** An active subscription whose `NextBillingAtUtc` has passed.
- **Test Steps:**
  1. Let the hourly cycle run (or force it in a test environment).
- **Expected Result:** New invoice generated for the next period; `NextBillingAtUtc` advances by the plan's billing cycle (Monthly/Quarterly/Yearly); one-time plans null out and don't rebill.
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

### TC-JOB-002 — Billing cycle is idempotent on repeated runs

- **Portal / Module:** `BillingBackgroundService`
- **Priority:** P1
- **Mode:** API
- **Test Steps:**
  1. Force the same cycle to run twice in a row without the billing window advancing.
- **Expected Result:** No duplicate invoice created for the same period — the idempotency check against the last auto-generated invoice holds.
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

### TC-JOB-003 — Overdue invoices auto-suspend the parent account

- **Portal / Module:** `BillingBackgroundService`
- **Priority:** P0
- **Mode:** API
- **Preconditions:** A Pending/PartiallyPaid invoice whose `DueDate` has passed.
- **Test Steps:**
  1. Let the cycle run.
- **Expected Result:** Invoice bulk-flipped to `Overdue`; a `FeeSuspension` is created for that parent if they have no active suspension yet — this is what actually triggers the Resources 400/Pay-Now block (cross-ref TC-PAR-011).
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

### TC-JOB-004 — Payment reminders send only when the toggle is on

- **Portal / Module:** `BillingBackgroundService` reminder pass
- **Priority:** P1
- **Mode:** API
- **Preconditions:** Settings → Notifications → "Fee payment reminders" on; an invoice due in 3 days or already overdue.
- **Test Steps:**
  1. Let the clock hit UTC hour 8.
- **Expected Result:** Reminder email sent; a per-recipient failure (bad address) doesn't block the rest of the batch.
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

### TC-JOB-005 — Payment reminders suppressed when the toggle is off

- **Portal / Module:** `BillingBackgroundService` reminder pass
- **Priority:** P1
- **Mode:** API
- **Preconditions:** Same toggle off.
- **Test Steps:**
  1. Repeat TC-JOB-004's conditions.
- **Expected Result:** No reminder emails sent — confirms the toggle actually gates the job, not just the UI.
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

### TC-JOB-006 — Missed webhooks are reconciled by direct gateway polling

- **Portal / Module:** `BillingBackgroundService` reconciliation pass
- **Priority:** P2
- **Mode:** API
- **Preconditions:** A pending gateway payment-link older than the poll window but within the reconciliation window (last 4 days, capped 200).
- **Test Steps:**
  1. Let the cycle run.
- **Expected Result:** Job polls the gateway directly and settles it if actually paid, catching a missed webhook.
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

### TC-JOB-007 — One bad account doesn't block other subscriptions in the same cycle

- **Portal / Module:** `BillingBackgroundService`
- **Priority:** P0
- **Mode:** API
- **Preconditions:** One department's payment account deactivated/misconfigured while other subscriptions are due in the same cycle.
- **Test Steps:**
  1. Let the cycle run.
- **Expected Result:** The bad account's subscription fails in isolation (per-subscription try/catch); all other subscriptions still bill correctly in the same cycle run.
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

### TC-JOB-008 — Session reminders send with a correctly scoped join link

- **Portal / Module:** `SessionReminderBackgroundService`
- **Priority:** P1
- **Mode:** API
- **Preconditions:** A session starting in 50–60 minutes.
- **Test Steps:**
  1. Let the 10-minute poll run.
- **Expected Result:** Teacher and batch parents (or demo lead) receive a reminder email with a token-scoped, non-moderator, time-limited (session end +2h) join link.
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

### TC-JOB-009 — Delayed-session alerts reach all active Admins

- **Portal / Module:** `SessionReminderBackgroundService`
- **Priority:** P2
- **Mode:** API
- **Preconditions:** A session that should have started 10–20 minutes ago with no `ActualStartAtUtc` (teacher never joined).
- **Test Steps:**
  1. Let the poll run.
- **Expected Result:** All active Admins receive a "delayed session" alert.
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

### TC-JOB-010 — Missed reminder windows are lost, not caught up

- **Portal / Module:** `SessionReminderBackgroundService`
- **Priority:** P2
- **Mode:** API
- **Test Steps:**
  1. Simulate a missed 10-minute reminder window (e.g. the service was down).
- **Expected Result:** Per documented behavior, that window is lost, not caught up on the next cycle — verify this is the actual (accepted) behavior rather than assuming a catch-up mechanism exists.
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

### TC-JOB-011 — Weekly digest sends only when the toggle is on

- **Portal / Module:** `ReportsDigestBackgroundService`
- **Priority:** P1
- **Mode:** API
- **Preconditions:** Notifications → "Weekly summary digest" on.
- **Test Steps:**
  1. Let the clock reach Monday 07:00 UTC.
- **Expected Result:** All active Admins receive the KPI digest (students, revenue collected/pending, enrollments, occupancy, conversion rate, refund rate, teacher utilization).
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

### TC-JOB-012 — Weekly digest suppressed when the toggle is off

- **Portal / Module:** `ReportsDigestBackgroundService`
- **Priority:** P2
- **Mode:** API
- **Preconditions:** Same toggle off.
- **Test Steps:**
  1. Repeat TC-JOB-011's conditions.
- **Expected Result:** No digest sent.
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

### TC-JOB-013 — Monthly progress-report drafts seed idempotently

- **Portal / Module:** `ProgressReportsBackgroundService`
- **Priority:** P1
- **Mode:** API
- **Preconditions:** Active children present.
- **Test Steps:**
  1. Let the clock reach the 1st of a month at 06:00 UTC.
  2. Force the same trigger condition to fire again.
- **Expected Result:** An empty draft `ProgressReport` seeded per active child on the first run; the second run doesn't duplicate drafts or touch already-sent reports (idempotent).
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

---

## 16. Cross-Portal End-to-End Scenarios (`TC-E2E`)

These walk the same underlying data through multiple portals and roles in sequence — this is
where integration bugs hide that per-screen testing can't catch (e.g. a status update in one
portal not reflecting in another that reads the same record). Run these with real browser
sessions per role, not just API calls, so the UI-level reflection is actually verified.

### TC-E2E-001 — Full demo→enrollment→billing→session→payout funnel

- **Portal / Module:** Marketing/Store → Admission → Teacher → Admin → Parent → Live Classroom → Payouts
- **Priority:** P0
- **Mode:** API
- **Preconditions:** A clean test environment with at least one course/batch template ready to receive a new enrollment.
- **Test Steps:**
  1. Public visitor books a free demo via `/store` (TC-MKT-003).
  2. Admission views it in Demo Scheduling; teacher is auto-assigned.
  3. Teacher delivers the demo and submits mandatory feedback (TC-TCH-005/006).
  4. Admission reviews the feedback in Leads, logs a follow-up, and generates a payment link (TC-ADS-011).
  5. Admin creates a new Parent account (credentials emailed); parent logs in and completes the mandatory Enrollment form (TC-PAR-002).
  6. Admin approves the enrollment form (TC-ADM-106), creating the Child record.
  7. Admin assigns the child to a batch (TC-ADM-033).
  8. Parent's Dashboard now shows the upcoming batch session.
  9. Parent pays the invoice via Pay Now (TC-PAR-007/008).
  10. Teacher delivers the first real class; both Teacher and Parent join the live classroom (TC-CLS-001/002).
  11. Teacher marks attendance and completes the session.
  12. The session accrues to the teacher's payout; Admin finalizes and marks it paid at month end (TC-ADM-079/081).
- **Expected Result:** Every step's output is visible and correct in the *next* portal in the chain — no stage silently drops data, no status is stuck showing a stale value in one portal while another has already moved on. This is the single most valuable test in the whole suite; run it before anything else if time is limited.
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

### TC-E2E-002 — Overdue → suspension → recovery

- **Portal / Module:** Billing background job → Parent → Admission → Admin
- **Priority:** P0
- **Mode:** API
- **Test Steps:**
  1. Force a parent's subscription invoice overdue (via `DueDate` in test data, or wait for `BillingBackgroundService`, TC-JOB-003).
  2. Confirm the parent's account auto-suspends; Resources returns 400 with a Pay Now message (TC-PAR-011).
  3. As the suspended parent, attempt to join a live session (TC-PAR-012).
  4. Parent pays via Cash (Pay Now → Cash).
  5. Admission confirms the cash intent (TC-ADS-010).
  6. Confirm the suspension auto-lifts.
  7. Confirm the parent regains Resources/session access without any separate manual Admin action.
- **Expected Result:** Suspension and recovery are consistent and automatic end-to-end; verify step 3's actual current enforcement state honestly (it's flagged as a Sprint 2 item — report what you actually observe, not what's intended).
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

### TC-E2E-003 — Permission propagation across roles

- **Portal / Module:** Admin → Permissions → Admission Team
- **Priority:** P0
- **Mode:** API
- **Test Steps:**
  1. Admin edits the "admission" role preset, removing `BillingFinance:Approve` (TC-ADM-022).
  2. A currently-logged-in Admission Team user reloads.
  3. That user attempts to confirm a pending cash intent (previously worked, TC-ADS-010).
  4. Admin restores the permission.
  5. User reloads again and retries.
- **Expected Result:** Permission removal takes effect (403) after reload; restoration re-enables the action after another reload — confirms the whole claims pipeline (Role preset → JWT claims → `[HasPermission]`) is live end-to-end, not just at initial login.
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

### TC-E2E-004 — No-show cascades produce distinct payout outcomes

- **Portal / Module:** Admin/Teacher → Sessions → Payouts
- **Priority:** P1
- **Mode:** API
- **Test Steps:**
  1. Schedule a session; mark a **teacher no-show** on it (TC-ADM-083).
  2. Verify: the session carries forward one week automatically, Admin receives an alert, the teacher's payout for that period reflects a deduction.
  3. Schedule another session; mark a **student no-show** on it.
  4. Verify: the session also carries forward, but the teacher's payout instead credits a "waiting amount" rather than a deduction.
- **Expected Result:** The two no-show types produce visibly different payout outcomes, both correctly reflected in Admin → Payouts and the Teacher's own Payout screen.
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

### TC-E2E-005 — Leave request → reschedule cascade agrees across three portals

- **Portal / Module:** Teacher → Coordinator → Admin/Parent
- **Priority:** P1
- **Mode:** API
- **Test Steps:**
  1. Teacher requests leave ≥6h before a scheduled session (TC-TCH-010).
  2. Coordinator approves it (TC-COR-005).
  3. Check the affected session's date on Admin's Academic Calendar, Parent's Schedule, and the Teacher's own My Classes.
- **Expected Result:** All three portals show the same new date for the rescheduled session — no portal is left showing a stale/conflicting date after the cascade completes.
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

### TC-E2E-006 — Batch completion → dormancy → recording window

- **Portal / Module:** Teacher → Admin → Parent
- **Priority:** P1
- **Mode:** API
- **Test Steps:**
  1. Teacher marks a batch's final scheduled session for its course complete (TC-ADM-036).
  2. Confirm the batch auto-moves to Dormant.
  3. Confirm Admin → Batches reflects Dormant status immediately.
  4. Confirm Parent → Resources → Recordings still shows recordings registered for that batch's sessions (until the window/job actually expires them — cross-ref TC-GAP-003 on whether expiry is actually enforced).
- **Expected Result:** Dormancy transition is immediate and correctly reflected; recording visibility behaves per Section 12.3/TC-GAP-003's documented caveat.
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

### TC-E2E-007 — Refund round-trip for both payment paths

- **Portal / Module:** Parent → Admin
- **Priority:** P1
- **Mode:** API
- **Test Steps:**
  1. Parent pays an invoice via a live gateway (Razorpay or Cashfree).
  2. Admin requests a refund (TC-ADM-062).
  3. Admin approves the refund (TC-ADM-063), triggering a real gateway call.
  4. Verify the refund appears correctly in Admin → Billing, and the invoice/payment history reflects the refunded state.
  5. Repeat steps 1–4 for a cash-paid invoice, confirming no gateway call occurs (TC-ADM-064).
- **Expected Result:** Refund state is consistent across the invoice detail, the Refunds list, and (if reachable) the parent-facing payment history — gateway-paid and cash-paid refunds are handled correctly by their distinct paths.
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

### TC-E2E-008 — Bulk communication reach matches the recipient-count preview

- **Portal / Module:** Admin → Bulk Email → Users
- **Priority:** P2
- **Mode:** API
- **Test Steps:**
  1. Admin sends a bulk email scoped to one specific batch (TC-ADM-095).
  2. Cross-check the Users directory for that batch's actual roster.
  3. Verify which parents actually received the email.
- **Expected Result:** Recipient count preview (TC-ADM-094) matches the actual delivered count exactly; no over- or under-reach.
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

### TC-E2E-009 — Sub Admin scoped view consistency after a preset change

- **Portal / Module:** Admin → Permissions → Sub Admin (all screens)
- **Priority:** P2
- **Mode:** API
- **Test Steps:**
  1. Admin creates a Sub Admin with a narrow preset (only Course/Batch + Session/Calendar modules).
  2. Sub Admin logs in, confirms Dashboard/Reports/Audit Log all correctly show only permitted data and hide the rest.
  3. Admin broadens the preset.
  4. Sub Admin reloads and confirms newly-granted screens now populate correctly without needing to log out/in again.
- **Expected Result:** Scoping is correct and updates propagate on reload per TC-PERM-006, consistent across every Sub Admin screen, not just the Permissions tab itself.
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

### TC-E2E-010 — Package subscription lifecycle across Admin, billing jobs, and Parent

- **Portal / Module:** Admin → Packages → Billing background job → Parent
- **Priority:** P1
- **Mode:** API
- **Test Steps:**
  1. Admin creates a monthly Package Plan and starts a Subscription for a student (TC-ADM-068/070).
  2. Let the billing cycle generate the first invoice on schedule (TC-JOB-001).
  3. Parent pays the invoice.
  4. Let a second billing cycle pass; confirm a second invoice generates automatically.
  5. Admin cancels the subscription (TC-ADM-071); confirm no further invoices generate afterward.
- **Expected Result:** Invoice generation, payment, and cancellation are all correctly reflected across Admin's Packages screen, the Parent's Billing screen, and the billing job's behavior — no invoice generated after cancellation, no missed invoice before it.
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

---

## 17. Known Issues / Expected-Current-Behavior Verification (`TC-GAP`)

These are **not exploratory** — they encode specific facts already established in
`Platform_Flow_Audit.md` and the sprint backlog. Run them to confirm the app is still in the state
the team believes it's in. If a case here now behaves *differently* than described (better or
worse), that's worth flagging explicitly as a regression or an unlogged fix — don't silently
assume the doc is right without checking the running app.

### TC-GAP-001 — Gateway fallback is no longer silent (Billing)

- **Portal / Module:** Admin → Payment Mapping / Parent → Pay Now
- **Priority:** P0
- **Documented status:** **Fixed** in the current codebase — was previously silent (fake `SIM-…` link, no error shown).
- **Test Steps:**
  1. Verify a department `PaymentAccount` with a placeholder/mismatched `GatewayProvider` no longer silently falls back to `SimulatedPaymentGateway` without any indication to the user/admin.
  2. Verify parent method-choice now routes correctly for Razorpay.
- **Expected Result:** Both hold true. Regression-test, don't assume still fixed forever.
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

### TC-GAP-002 — Manual payment dedupes against pending cash intents (Billing)

- **Portal / Module:** Admin → Billing
- **Priority:** P1
- **Documented status:** **Fixed** — previously created a duplicate row alongside the real Success one.
- **Test Steps:**
  1. Verify manual "Record Payment" against an invoice with a matching pending cash intent settles that same row instead of creating a duplicate orphaned Pending transaction.
- **Expected Result:** Confirmed fixed.
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

### TC-GAP-003 — Recording 15-day auto-expiry job may not actually run (Recordings)

- **Portal / Module:** Parent → Resources → Recordings
- **Priority:** P1
- **Documented status:** **Open / Not Started** per sprint backlog as of this audit.
- **Test Steps:**
  1. Verify whether the 15-day recording auto-expiry job actually runs (deletes/hides recordings after 15 days) or only the UI-filter/DB-field logic exists without a live scheduled deletion.
  2. Test both "does it disappear from parent view" and "does the underlying storage object actually get deleted at 16 days" separately.
- **Expected Result:** Recordings may not currently actually expire even though they're designed to — document precisely which half (visibility vs. storage deletion) is and isn't working, since they may diverge.
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

### TC-GAP-004 — Integration secret fields mask on entry, not just on read (Settings)

- **Portal / Module:** Admin → Settings → Integrations
- **Priority:** P2
- **Documented status:** **Fixed** — previously plaintext-on-entry.
- **Test Steps:**
  1. Verify secret fields (Razorpay/Cashfree keys, etc.) render as masked (`type="password"` + show/hide) while typing, not just on subsequent read.
- **Expected Result:** Confirmed fixed.
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

### TC-GAP-005 — Admission payment-link table: real link generation, verify no residual mock path (Admission)

- **Portal / Module:** Admission → Payments
- **Priority:** P2
- **Documented status:** **Partially fixed** — link-generation button is real; the rest of the payment-link table/workflow (e.g. "Remind") was still flagged as partially mock as of this audit.
- **Test Steps:**
  1. Verify "Copy link" calls the real `POST /api/invoices/{id}/payment-link` endpoint.
  2. Check every other action in the same table (Remind, status display, etc.) for any remaining dead/mock code path.
- **Expected Result:** Link generation is confirmed real; document the current state of every other action in the table precisely, since full fix status is unconfirmed.
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

### TC-GAP-006 — Management has no BillingFinance grant (by design) (Permissions)

- **Portal / Module:** Management (all screens)
- **Priority:** P1
- **Documented status:** **By design, open policy question** — not a bug.
- **Test Steps:**
  1. Verify Management role has no `BillingFinance` grant of any kind and cannot confirm cash or approve refunds.
- **Expected Result:** Confirmed enforced as-is. Don't "fix" this without a product decision; the test case is to confirm it stays enforced.
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

### TC-GAP-007 — "Enrolled" subtitle is descriptive only, not payment-verified (Admission)

- **Portal / Module:** Admission → Conversion
- **Priority:** P1
- **Documented status:** **Still open** as of this audit.
- **Test Steps:**
  1. Verify the Conversion Kanban's "Enrolled" column subtitle text ("payment received") is descriptive only and not backed by an actual payment-status check.
  2. Confirm a lead can display as payment-received while genuinely unpaid.
- **Expected Result:** Confirmed still open.
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

### TC-GAP-008 — JWT-secured Jitsi rooms / lobby enforcement (Live Classroom)

- **Portal / Module:** Live Classroom / Jitsi
- **Priority:** P1
- **Documented status:** Listed as **Sprint 2 production-hardening scope** — verify current actual state.
- **Test Steps:**
  1. Verify whether JWT-secured Jitsi rooms (prosody `token_verification`) and enforced secure-domain lobby/waiting-room behavior are actually implemented in the current deployment.
- **Expected Result:** Document current actual state precisely — an unauthenticated join risk exists until these are implemented, so this is worth confirming rather than assuming either way.
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

### TC-GAP-009 — Cancelled invoices display incorrectly to parents (Billing)

- **Portal / Module:** Parent → Billing / `INVOICE_STATUS_FROM_API`
- **Priority:** P0
- **Documented status:** **Still open** — needs a `FeeStatusBadge` variant for Cancelled. Highest-priority currently-known open defect in the whole audit.
- **Test Steps:**
  1. Verify `Invoice.Cancelled` status is silently mapped to `"pending"` in the frontend's `INVOICE_STATUS_FROM_API`.
  2. Confirm a cancelled invoice currently displays to a parent as still awaiting payment, with no distinct badge state.
- **Expected Result:** Confirmed still open. Treat any deployment plan as blocked on this if real cancellations are expected to occur.
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

### TC-GAP-010 — Gamification beyond stars/leaderboard is not implemented (Live Classroom)

- **Portal / Module:** Live Classroom / Student
- **Priority:** P2
- **Documented status:** **Confirmed not implemented** as of this audit.
- **Test Steps:**
  1. Verify quizzes, badges, drag-and-drop whiteboard activities, and reward mechanics beyond the basic star/leaderboard covered in Section 12.2 remain not implemented (Sprint 3–4 scope).
- **Expected Result:** Don't file "missing feature" bugs for this scope; do flag if something appears to exist but is broken, since that would indicate an in-progress partial implementation worth knowing about.
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

### TC-GAP-011 — Student-level analytics are likely not yet implemented (Reporting)

- **Portal / Module:** Admin/Management → Reports
- **Priority:** P2
- **Documented status:** **Sprint 4 scope, likely not yet implemented** — schema lands with the gamification sprint.
- **Test Steps:**
  1. Verify current implementation state of student-level analytics (participation, click activity, quiz scores, whiteboard interaction, attention/engagement scoring) referenced in `ANALYTICS_KPIS.md`.
- **Expected Result:** Verify rather than assume; document exactly what exists vs. what's still absent.
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

### TC-GAP-012 — Coordinator calendar-conflict checking may be shallow (Coordinator)

- **Portal / Module:** Coordinator → Calendar
- **Priority:** P2
- **Documented status:** Flagged as **Not Started** in the backlog as of this audit — UI is present, deeper conflict validation may not be.
- **Test Steps:**
  1. Verify current backend calendar-conflict checking for Coordinator's reschedule/holiday actions, beyond the teacher double-booking guard already covered in TC-ADM-032.
- **Expected Result:** Document current actual state.
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

### TC-GAP-013 — Plain-text temp passwords, no forced first-login change (Security)

- **Portal / Module:** Admin → Users → credential delivery
- **Priority:** P1
- **Documented status:** **Documented, accepted risk** for the current sprint — Sprint 2 scope to harden.
- **Test Steps:**
  1. Verify temporary passwords are delivered in plain text via email/WhatsApp on account creation (cross-ref TC-SEC-011).
  2. Verify there is no forced first-login password change yet.
- **Expected Result:** Confirm it matches this documented state; don't treat as a new finding unless it has regressed to something worse (e.g. password logged server-side, which would be new).
- **Actual Result:** _(fill in during execution)_
- **Status:** ☐ Pass ☐ Fail ☐ Blocked ☐ Not Run

---

## Appendix A: Coverage Summary

365 test cases across 17 sections, covering all 64 frontend screens (8 role portals + auth +
marketing + live classroom), all 27 backend controllers, all 4 background jobs, and the SignalR
hub — each now written as an expanded block with Preconditions, Test Data (where relevant),
numbered Test Steps, and Expected Result, plus blank Actual Result / Status fields for execution
tracking.

| Section | Cases | Priority-0 count | Notes |
|---|---|---|---|
| 1. Authentication | 22 | 5 | Login, PIN reset, token lifecycle, rate limiting, session persistence |
| 2. Authorization / Permissions | 20 | 5 | Cross-portal RBAC matrix, consistency and propagation checks |
| 3. Admin Portal | 124 | 20 | Largest portal — 20 screens |
| 4. Sub Admin Portal | 11 | 2 | Scoped-permission-matrix testing pattern |
| 5. Coordinator Portal | 10 | 3 | |
| 6. Management Portal | 8 | 1 | Read-only-by-design verification |
| 7. Teacher Portal | 17 | 5 | Demo-feedback gate, 6-hour leave rule |
| 8. Parent Portal | 23 | 7 | Enrollment gate, Pay Now, fee suspension |
| 9. Student Portal | 6 | 0 | |
| 10. Admission Portal | 16 | 5 | Full funnel |
| 11. Marketing & Store | 7 | 2 | Public, unauthenticated |
| 12. Live Classroom / SignalR | 30 | 4 | Highest technical complexity |
| 13. Billing & Payments Deep-Dive | 18 | 6 | Highest financial risk |
| 14. Security | 16 | 5 | |
| 15. Background Jobs | 13 | 3 | |
| 16. Cross-Portal E2E | 10 | 3 | Highest integration-risk value per case |
| 17. Known Issues Verification | 13 | 3 | Confirms documented state, not exploratory |

**Suggested execution order if time-boxed**: Section 16 (E2E) first for a fast signal on whether
the core funnel works at all → Section 1–2 (Auth/Permissions) since everything else depends on
access actually being correct → Section 13/14 (Billing/Security) for financial and data-exposure
risk → Section 17 (Known Issues) to confirm nothing has silently regressed → remaining portal
sections in whatever order matches the current sprint's focus area.

## Appendix B: Gaps in this suite (be aware, don't assume covered)

- **No automated tests are written here** — this is a manual/exploratory test *case* suite. Per
  `TEST_STRATEGY.md`, Playwright E2E automation is Sprint 2+ scope and currently has no config or
  test files; `iucs.readernest.tests` (xUnit) covers services, not these user flows.
- **Load/concurrency testing is explicitly out of scope** here (TC-CLS-026 notes this) — that's a
  Sprint 5 k6/`jitsi-meet-torture` activity per `TEST_STRATEGY.md`, not something this suite
  attempts to substitute for.
- **Visual/accessibility regression** (screen-reader support, color contrast, keyboard-only
  navigation) is not covered — this suite focuses on functional correctness and data integrity.
- **Real third-party gateway behavior** (actual Razorpay/Cashfree production quirks beyond what
  their test-mode sandboxes reproduce) can't be fully verified without production credentials —
  TC-BIL-008 flags the one specific area (Cashfree refund order-id handling) already known to need
  this kind of verification.
- This suite was authored by reading code and existing docs, not by running the application. Some
  "expected results" describe intended/documented behavior — where you find the actual behavior
  differs, that's exactly the kind of finding this document exists to surface; update the relevant
  Actual Result / Status fields when you do, rather than treating the Expected Result column as
  ground truth.