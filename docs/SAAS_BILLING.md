# SaaS billing: plans, subscriptions, entitlements, reminders

This layer lets the platform sell the application to tenants (academies) on recurring plans. It lives in the same
API process as everything else (no separate service) and stores its data in the **master database**, next to
`tenants`. All new tables and columns are `snake_case`.

> Do not confuse it with `/api/subscriptions` and `/api/package-plans`. Those bill **parents inside one academy** and
> live in each tenant's database. The SaaS layer bills the **academy itself**.

## What was added

| Area | Where |
|---|---|
| Entities (14 tables) | `iucs.meettomanage.domain/Entities/Saas/` |
| Mapping + snake_case rules | `Data/Configurations/SaasModelConfiguration.cs`, `Data/MasterDbContext.cs` |
| Migration | `Migrations/Master/*_AddSaasBilling.cs` (SQL preview: `docs/saas-billing-schema.sql`) |
| Business rules (pure, unit-tested) | `iucs.meettomanage.api/Saas/SaasRules.cs` |
| Services | `Saas/SaasSubscriptionService.cs`, `EntitlementService.cs`, `SaasReminderService.cs`, `SaasUsageService.cs` |
| Workers | `Saas/SaasWorkers.cs` (billing cycle + reminders every 15 min; usage snapshot hourly) |
| Limit enforcement | `Saas/EntitlementEnforcement.cs` (save interceptor + `[RequiresFeature]`) |
| Controllers | `PlatformSaasController` (operator), `PublicSaasController` (plans, signup), `PlatformBillingWebhookController`, `TenantBillingController` (client admin) |
| Frontend | `features/platform/{Plans,Subscriptions,Revenue,Reminders}.tsx`, `features/admin/Subscription.tsx`, `features/public/Signup.tsx`, `components/SubscriptionBanner.tsx` |

## Tables (master database)

`plan`, `plan_price`, `plan_entitlement`, `tenant_subscription` (one row per tenant), `tenant_subscription_event`
(audit trail), `tenant_entitlement_override`, `platform_invoice`, `platform_invoice_line`, `platform_payment`,
`platform_webhook_event` (idempotency), `tenant_billing_profile`, `tenant_usage_snapshot`,
`subscription_reminder_log`, `platform_email_template`, plus the sequence `platform_invoice_number_seq`.
Foreign keys point at the existing `tenants` table, which is not modified.

## Concepts

* **Plan / price / entitlement** are data. A price change creates a **new** `plan_price` row and retires the old
  one, so existing subscribers keep what they signed up at (grandfathering).
* **Entitlements** are limits (`max_students`, `max_staff`, `max_storage_mb`, `max_session_minutes_per_month`,
  `recording_retention_days`; `-1` = unlimited) and features (`feature_*`, `1`/`0`). A tenant's overrides win over its plan.
* **Legacy plan.** Every tenant that existed before billing is moved to the hidden, unlimited `legacy` plan at start-up,
  so nothing changes for them until an operator assigns a plan.
* **Subscription states:** `Trialing -> Active -> PastDue -> Suspended`, plus `Expired` (trial ended) and `Cancelled`.
  The billing cycle drives `Tenant.Status` (so suspended tenants cannot sign in), but it only reactivates a tenant
  **it** suspended (`tenant_subscription.suspended_by_billing`). A tenant an operator suspended by hand stays suspended.

### Money rules

* GST is added on top of the plan price (`SaasBilling:TaxRatePercent`, default 18).
* A renewal invoice is issued `InvoiceLeadDays` (7) before the period ends, due on the period end date.
* If it is unpaid at period end the subscription becomes `PastDue` with `GraceDays` (7) of grace, then `Suspended`.
* **Upgrade** (or first purchase): invoice = new price minus credit for the unused part of the current period; the new
  period starts when it is paid. **Downgrade:** scheduled for the next renewal and refused if current usage does not fit.
* Paying an invoice is idempotent (a single conditional `UPDATE ... WHERE status = 'Open'`), so webhook retries and
  double clicks cannot double-apply it.

### Enforcement

* Students and staff limits are enforced centrally by `EntitlementInterceptor` on every save from a live HTTP request
  (not workers or seeding). Over the limit returns **HTTP 402** with a plain message. It runs before the login-directory
  interceptor, so a rejected save leaves no orphan e-mail reservation.
* If the master database cannot be read, the check **fails open** (a billing outage never stops teaching).
* Storage and session-minute limits are **soft**: shown to the client and reminded at 80% / 100%, not blocked.
* `[RequiresFeature("feature_x")]` gates an endpoint on a plan feature (402 otherwise). It is available but not yet
  applied to any existing controller; apply it where a feature should be plan-restricted.

### Reminders

Sent by e-mail from `SaasBilling:Smtp` (the platform's own mailbox, never a tenant's). Every send is first recorded in
`subscription_reminder_log` under a unique `(subscription, rule, cycle, channel)` key, so a reminder goes out at most once
even with several API instances. If SMTP is not configured the row is stored as `Skipped` with the reason, visible in
**Auto-reminders** in the console. Rules: trial started / ending (7, 3, 1 days) / expired, renewal invoice, payment
received / failed, overdue (days 1, 3, 5), grace ending (48 h, 24 h), suspended, reactivated, plan changed, downgrade
scheduled, cancelled, usage at 80% / 100%. All wording is editable in the console (`platform_email_template`).

## Configuration (`SaasBilling` section)

| Key | Default | Meaning |
|---|---|---|
| `Gateway` | `Manual` | `Manual` (operator marks invoices paid) or `Razorpay` (payment link + webhook) |
| `Razorpay:KeyId / KeySecret / WebhookSecret` | empty | Use user-secrets or environment variables |
| `Smtp:Host / Port / Username / Password / From / UseTls` | empty | Platform mailbox for reminders |
| `GraceDays`, `InvoiceLeadDays`, `TaxRatePercent`, `DefaultTrialDays` | 7, 7, 18, 14 | Billing policy |
| `DefaultPlanForNewTenants` | `trial` | Plan for operator-created tenants (an attached existing database gets `legacy`) |
| `SignupEnabled` | `false` | Opens the public `/signup` page |
| `SupportEmail`, `PlatformName`, `InvoicePrefix` | | Shown in e-mails and invoices |

Razorpay webhook URL: `https://<api-host>/api/platform/billing/webhooks/razorpay`, events `payment_link.paid` and
`payment.failed`. Requests with a bad signature get 401.

## Rolling it out

1. Deploy. With `Database:MigrateOnStartup=true` the master migration `AddSaasBilling` is applied and the seeder runs
   (plans, templates, `legacy` subscription for every existing tenant). If migrations are run by CI, apply
   `Migrations/Master` first, then start the API.
2. In **Plans** (platform console) review the seeded **placeholder prices** and limits and set your real ones.
3. Set `SaasBilling:Smtp:*` (otherwise reminders are logged as Skipped) and, for online payments, `Gateway=Razorpay`.
4. Move a tenant onto a plan in **Subscriptions & invoices -> Manage** (Invoice, Complimentary or Trial).
5. Optional: set `SaasBilling:SignupEnabled=true` to open `/signup`.

The tenant administrator sees **My Subscription** (Finance menu) and a banner for trial / overdue / ending states.

## Operating notes and known limits

* **Suspended clients cannot sign in**, so they cannot open the billing page. They pay through the payment link in the
  reminder e-mail (needs `Gateway=Razorpay`), or the operator marks the invoice paid, which reactivates them.
* Collection is **invoice per cycle** (self-managed). Gateway auto-debit mandates are not implemented.
* Reminders are e-mail only. WhatsApp / SMS, coupons, add-ons, usage-based overage, custom domains, SSO and separate
  platform roles (Support / Finance) are not built; the schema leaves room for them.
* Self-signup does not verify the e-mail address or use a CAPTCHA yet. It is rate-limited and allows one trial per
  e-mail, and it is **off by default**; enable it only once you are comfortable with that.
* GST is a single rate; there is no CGST/SGST/IGST split by state.
* The billing workers take a PostgreSQL advisory lock, so running several API instances is safe.
