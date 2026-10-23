---
sidebar_position: 10
title: Errors
---

# Errors

Every API answers errors in the same shape as LoginLink and CrudApi:

```json
{ "error": "live_not_ready", "error_description": "Live payments need at least one payment method with Live settings that passed their check." }
```

`error` is stable for code to check; `error_description` is for people and may change.

| Status | `error` | |
|---|---|---|
| 400 | `invalid_request` | A field is missing or wrong; the description says which. |
| 400 | `invalid_json` | The body isn't valid JSON. |
| 400 | `invalid_query` | A reporting API filter or parameter is wrong. |
| 400 | `live_not_ready` | No method has passing Live settings yet. |
| 400 | `link_closed` | The payment link is off, expired or used up. |
| 401 | `invalid_key` / `invalid_token` | No key, an unknown key, or a revoked or expired one. |
| 403 | `wrong_key_type` | `sk_` keys are for `/api/v1`, `tdk_` keys for `/api/tenant`, `pdk_` keys for `/api/admin`. |
| 403 | `insufficient_scope` | The key lacks the capability the route needs. |
| 403 | `tenant_suspended` | The platform suspended this account. |
| 403 | `owner_only` | Only an owner can make someone an owner, in the console. |
| 404 | `not_found` | No such object (or outside this key's reach). |
| 409 | `idempotency_conflict` | The `Idempotency-Key` was used with a different body. |
| 409 | `reference_conflict`, `code_taken`, `conflict` | Already exists. |
| 409 | `final` | The payment's status is final. |
| 409 | `processing` | The payment is `processing` and can't be cancelled. |
| 409 | `not_paid`, `refunds_not_supported` | See [refunds](refunds.md). |
| 409 | `last_owner` | The account would have no owner left. |
| 422 | `statement_unreadable`, `report_unreadable`, … | An uploaded file couldn't be read; the description says why. |
| 429 | `quota_exceeded` | A platform key's tenant-creation limit is used up. |
| 503 | `setup_required` | PayLink isn't set up yet. |

Every call made with a tenant's keys is listed (without bodies) in **Console → Developers → API request log**, with its error code.
