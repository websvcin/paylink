---
sidebar_position: 5
title: Core concepts
---

# Core concepts

## Test and live

Every tenant has two separate worlds. **Test** works from the first minute with the built-in Simulator; **Live** takes real money. They have separate method settings, keys (`sk_test_` / `sk_live_`), webhook endpoints, links and data. The console has a Test/Live switch; the automation API takes `?livemode=true`.

Live payments are refused (`live_not_ready`) until at least one method has Live settings that passed their check.

## Payments, attempts and the journey

- A **payment** is one amount to collect. It has a `reference` (your order id), an optional customer, `metadata`, an expiry (30 minutes by default) and a status.
- An **attempt** is one try with one method. A failed attempt sends the payment back to `requires_action` so the payer can try again; after 5 attempts the payment fails with `too_many_attempts`.
- The **journey** records every event: created, checkout opened, method chosen, gateway notifications (verified or not), risk decisions, approvals, refunds, webhooks sent.

## Statuses

`created` → `requires_action` → `processing` → `succeeded` | `failed` | `cancelled` | `expired`, plus `on_hold` when a risk rule asks for review. See [statuses and failure reasons](../integrate/statuses.md).

## Amounts

Amounts are integers in minor units: paise for INR, so `149900` is ₹1,499.00. Currencies are ISO 4217 codes; each method lists the currencies it takes.

## Roles in a tenant

| Role | Can |
|---|---|
| `owner` | Everything, including making other owners. |
| `admin` | Everything except making owners: methods, team, keys, settings. |
| `developer` | Keys, webhooks and the API request log. |
| `finance` | Payments, refunds, UPI verification, reconciliation, disputes. |
| `viewer` | Read only. |

Platform admins have `admin` (everything) or `support` (read-only) access to `/admin`.

## Keys

| Prefix | Belongs to | Used for |
|---|---|---|
| `sk_test_{tenant}_…`, `sk_live_{tenant}_…` | A tenant | The [Payments API](../integrate/payments-api.md) (`/api/v1`) from the merchant's server. |
| `tdk_{tenant}_…` | A tenant | The [automation API](../automation/tenant-api.md) (`/api/tenant`), limited to chosen capabilities. |
| `pdk_…` | The platform | The [platform API](../automation/platform-api.md) (`/api/admin`), and with *Act for tenants*, any tenant's APIs. |
| `whsec_…` | A webhook endpoint | Verifying the `PayLink-Signature` on webhooks. |

Every key is shown once and stored only as a SHA-256 hash.
