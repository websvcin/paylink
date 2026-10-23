---
sidebar_position: 1
title: Automation API (tenant)
---

# Automation API (tenant)

**Everything a tenant's team can do in the console has a route here**, for scripts, back-office tools and other systems: approve UPI payments, upload bank statements, create payments and links, refund, set up methods and routing, webhooks, risk rules, alerts, team, domain, reports, reconciliation, keys and the audit log.

Base path `/api/tenant`. Authenticate with a **developer key**:

```
Authorization: Bearer tdk_acme_…
```

Create one in **Console → Developers → Automation keys** and tick its capabilities (or **Everything**). Choose test or live data with `?livemode=true|false` (default `false`). JSON is snake_case; amounts in paise. A route the key has no capability for answers `403 insufficient_scope`.

A platform key with *Act for tenants* can call every route here for any tenant in its reach with `PayLink-Tenant: {slug}`. See [building on PayLink](building-on-paylink.md).

## Routes by capability

| Capability | Routes |
|---|---|
| `payments.read` | `GET /overview` · `GET /payments` (`status`, `q`, `method`, `from`, `to`, `min_amount`, `max_amount`, `reference`, `limit`, `offset`; answers `total` and `total_amount`) · `GET /payments/export` (CSV, same filters) · `GET /payments/{id}` (with the full journey) |
| `payments.write` | `POST /payments` (create; `Idempotency-Key`) · `POST /payments/{id}/cancel` · `POST /payments/{id}/release` (a held payment) |
| `refunds` | `POST /payments/{id}/refunds` `{ "amount", "reason", "manual" }` (manual refunds need a reason) |
| `upi.verify` | `GET /upi/pending` · `POST /upi/{attempt_id}/approve` and `/reject` `{ "note" }` · `POST /upi/statements` (bank statement CSV) · `GET /upi/decided` · `GET /payments/{id}/attempts/{attempt_id}/proof` (screenshot) |
| `links` | `GET/POST /links` · `GET /links/{id}` · `PATCH /links/{id}` `{ "active" }` · `GET /links/{id}/qr` (SVG) |
| `reports` | `GET /reports` (`range` or `from`/`to`) |
| `reconciliation` | `POST /settlements` (settlement CSV) · `GET /settlements` (imports, lines that need a look, daily close, unsettled payments) |
| `disputes` | `GET /disputes` · `POST /disputes/{id}/note` |
| `methods` | `GET /methods` · `PUT /methods/{connector}/{test|live}` `{ "enabled", "settings" }` · `GET/PUT /routing` · `GET/POST /account-requests` |
| `webhooks` | `GET/POST /webhooks` · `DELETE /webhooks/{id}` · `POST /webhooks/{id}/test` · `GET /webhooks/deliveries` · `POST /webhooks/deliveries/{id}/redeliver` |
| `risk` | `GET/POST /risk-rules` · `DELETE /risk-rules/{id}` |
| `alerts` | `GET/POST /alerts` · `DELETE /alerts/{id}` |
| `team` | `GET /team` · `PUT /team/admission` · `POST /team/members/{id}` `{ "decision": "approve"|"remove", "role" }` · `PUT /team/role-map` |
| `domain` | `GET/PUT/DELETE /domain` · `POST /domain/verify` |
| `data_access` | `GET/DELETE /data-access` · `PUT /data-access/reader` · `POST /data-access/register` |
| `keys` | `GET/POST /keys` · `DELETE /keys/{id}` · `GET /requests` (API request log) |
| `audit` | `GET /audit` (`limit`, `verify=true`) |

Every request and answer is in the [tenant API reference](../api-reference/tenant-api.md).

## Rules the API keeps

The API calls the same code as the console, so the same rules apply and every action is in the audit log under the key's name. Some extras:

- **Secrets are never returned.** Method settings come back without their secret fields.
- **Keys can't escalate.** A key with *keys* can create secret keys, and developer keys with at most its own capabilities.
- **Keys can't make owners**, and can't remove or demote the last owner.

## Examples

```bash
# Turn on PayLink UPI in Test
curl -X PUT https://pay.example.com/api/tenant/methods/upi/test \
  -H "Authorization: Bearer tdk_acme_…" -H "Content-Type: application/json" \
  -d '{ "enabled": true, "settings": { "vpa": "acme@okhdfcbank", "payee_name": "Acme Learning", "business_vpa_confirmed": "true" } }'

# Route: UPI first up to ₹1,00,000, failover on
curl -X PUT https://pay.example.com/api/tenant/routing -H "Authorization: Bearer tdk_acme_…" -H "Content-Type: application/json" \
  -d '{ "methods": [ { "connector": "upi", "order": 1, "max_amount": 10000000 } ], "failover": true }'

# A payment link
curl -X POST https://pay.example.com/api/tenant/links -H "Authorization: Bearer tdk_acme_…" -H "Content-Type: application/json" \
  -d '{ "title": "Term fees", "amount": 150000, "code": "term-fees" }'

# Last month's succeeded payments as CSV
curl -o october.csv "https://pay.example.com/api/tenant/payments/export?livemode=true&status=succeeded&from=2026-10-01&to=2026-10-31" \
  -H "Authorization: Bearer tdk_acme_…"

# Hold anything above ₹50,000 for review
curl -X POST https://pay.example.com/api/tenant/risk-rules -H "Authorization: Bearer tdk_acme_…" -H "Content-Type: application/json" \
  -d '{ "kind": "amount_above", "value": "50000", "action": "review", "note": "Large payments" }'

# Approve a new team member as finance
curl -X POST https://pay.example.com/api/tenant/team/members/7 -H "Authorization: Bearer tdk_acme_…" -H "Content-Type: application/json" \
  -d '{ "decision": "approve", "role": "finance" }'
```
