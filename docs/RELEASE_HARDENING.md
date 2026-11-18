> **Reverted for now (on request):** the infra-config move (`appsettings.infra.json`, Jenkins mount/check) and the
> clearing of stored PINs after first sign-in. Staff can view stored PINs as before. The weak seed-PIN check now only
> logs a warning. The sections below about those items describe the intended later state.

# Release hardening: what changed and what the operator must do

## Before the next deploy (required)

1. **Create the infrastructure config on the Docker host.** The monitored-server list, Prometheus URL and
   Jibri IPs are no longer in `appsettings.json`. Copy `iucs.meettomanage.api/appsettings.infra.example.json`
   to `/opt/meettomanage/appsettings.infra.json` on the host and fill in the real values. The Jenkins deploy
   mounts it read-only and **fails early if it is missing**. Without it, Jibri recording access
   (`Jibri:AllowedIps`) and server monitoring would stop working. Path is set by `INFRA_CONFIG_HOST_PATH`
   in the Jenkinsfile.
2. **Payments in Production.** The simulated gateway is now refused in Production: if a gateway is not
   enabled and fully configured in Settings -> Integrations, parents see "Online payment is not set up yet"
   instead of a fake link. Verify Razorpay/Cashfree are enabled for every live academy. A demo environment
   can opt back in with `Payments__AllowSimulatedGateway=true`.
3. **Seed admin PIN.** Production refuses to start with a well-known `Seed:AdminPin` (1234, 0000, ...).
   The base `appsettings.json` no longer carries one. In Production the seed PIN is applied only when the
   admin is first created; it no longer overwrites an admin's own PIN on every restart. To force a reset
   once, set `Seed__ForcePinConvergence=true` for that deploy.
4. **CORS.** The base config no longer allows `*`. Production already sets `Cors__AllowedOrigins__0`.

## New

- `POST /api/auth/me/delete-account` (parent, PIN-confirmed) and a "Delete account" row in the mobile app
  (More). Uses the existing audited hard-delete (every row snapshotted to the deletion log).
  Still to do: a public web page/URL for deletion requests (Google Play asks for one in the Data safety form).
- A temporary PIN's recoverable copy is cleared as soon as the user signs in with it.
- `dotnet iucs.meettomanage.api.dll migrate-tenants` migrates the master and every tenant database, then
  exits (non-zero if any tenant failed). To use it: run it as a deploy step, then start the API with
  `Database__MigrateOnStartup=false`. The Jenkins deploy still migrates on startup until you switch it.
- `ops/backup/pg-restore-drill.sh` (weekly restore test) and `ops/backup/offsite-sync.sh` (copy off-server).
  See `ops/backup/README.md`.
- Mobile: `eas.json` has iOS build/submit profiles; `.github/workflows/mobile.yml` runs checks and EAS
  builds on a `v*` tag (needs the `EXPO_TOKEN` secret). Replace `ascAppId` in `eas.json` with the real
  App Store Connect app id.

## Not done yet

- Web auth tokens are still in browser storage; the CSP still allows `'unsafe-inline'` scripts
  (the index page has inline scripts that need hashes or nonces first).
- Reverse-proxy trust: forwarded headers are trusted from any address, so the API port must only be
  reachable through your proxy, or per-IP rate limits can be spoofed.
- The restore drill and off-site copy have not been run against real backups.

## GST on platform invoices (before issuing real invoices)

Subscription invoices now carry the fields a GST tax invoice needs, and split the tax by place of supply:
CGST + SGST when the academy is in the same state as you, IGST when it is in another state. The academy's state comes
from its GSTIN, or failing that the state on its billing profile; an unknown state is treated as your own.

Set these on the API (env vars shown). Without `SaasBilling__SellerGstin` invoices print as plain "Invoice" with one
flat GST line, because there is no supplier state to split against:

| Setting | Purpose |
|---|---|
| `SaasBilling__SellerGstin` | Your GSTIN. The supplier state is read from its first two digits. |
| `SaasBilling__SellerLegalName` | Legal name printed as supplier. |
| `SaasBilling__SellerAddress` | Address printed as supplier. |
| `SaasBilling__SacCode` | Defaults to `998431`. **Confirm the right SAC with your CA.** |

The values are copied onto each invoice when it is issued, so later changes don't alter invoices already sent.
Invoices issued before this change keep the single GST line. Migration `AddGstInvoiceFields` adds the columns
(it runs with `migrate-tenants` / on startup like any other).

Not yet covered: credit notes and refunds for platform invoices, e-invoicing (IRN/QR), and export (non-India) supply.
