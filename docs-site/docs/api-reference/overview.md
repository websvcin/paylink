---
sidebar_position: 1
title: Overview
---

# API reference

PayLink has three HTTP APIs. Each has its own kind of key, and each is described by an **OpenAPI 3** document, so you can generate a client in any language.

| API | Base path | Key | For | Reference | OpenAPI |
|---|---|---|---|---|---|
| **Payments API** | `/api/v1` | `sk_test_…` / `sk_live_…` | A merchant's server: payments, refunds, links, disputes, reporting | [Payments API](payments-api.md) | [payments.json](pathname:///openapi/payments.json) |
| **Tenant API** | `/api/tenant` | `tdk_…` | Scripts that run a tenant's console: everything the console does | [Tenant API](tenant-api.md) | [tenant.json](pathname:///openapi/tenant.json) |
| **Platform API** | `/api/admin` | `pdk_…` | Running PayLink: tenants, their keys, approvals, audit; acting for tenants | [Platform API](platform-api.md) | [platform.json](pathname:///openapi/platform.json) |

Every PayLink serves the same documents at `/docs/api/{payments|tenant|platform}/openapi.json`, with **Swagger UI at `/docs/api`** where you can try calls with your own key.

## Conventions

- **HTTPS and JSON.** Request and response bodies are JSON in `snake_case` (the platform API answers in `camelCase`, like LoginLink's).
- **Bearer keys.** `Authorization: Bearer {key}`. Keys are shown once and stored as hashes.
- **Amounts** are integers in minor units (paise); **currencies** are ISO 4217 codes; **times** are ISO 8601 in UTC.
- **Idempotency.** `Idempotency-Key` on creates; the same key and body returns the first result (`Idempotent-Replayed: true`).
- **Test and live.** `sk_test_` keys see only test data; on `/api/tenant`, `?livemode=true|false`.
- **Errors** are `{ "error": "code", "error_description": "…" }` with the right HTTP status. See [errors](../integrate/errors.md).
- **Acting for a tenant.** A platform key with *Act for tenants* adds `PayLink-Tenant: {slug}` (and `PayLink-Mode: test|live` on `/api/v1`). See [building on PayLink](../automation/building-on-paylink.md).

## Not APIs

The browser pages are for people and sign in through LoginLink: `/admin` (platform console), `/{slug}/console` (tenant console), `/signup`, `/setup`, `/pay/{id}` and `/l/{code}` (payer pages). Gateways call `/hooks/{tenant}/{connector}/{mode}`. None of these take API keys.
