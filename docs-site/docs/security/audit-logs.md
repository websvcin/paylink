---
sidebar_position: 2
title: Audit logs
---

# Audit logs

PayLink keeps two kinds of audit log, plus each payment's journey.

| Log | Where | What |
|---|---|---|
| **Platform audit log** | **Admin → Audit log**, `GET /api/admin/audit` | Setup, platform settings, admins, tenants created, suspended or moved, connector catalog changes, partner programmes, platform keys, backup downloads. |
| **Tenant audit log** | **Console → Audit log**, `GET /api/tenant/audit` | Sign-ins, team and role changes, method settings, keys, refunds, UPI decisions, exports, screenshot views, risk rules, alerts, domain and data-access changes, calls made by keys and by platform keys acting for the tenant. |
| **Payment journey** | Each payment, `GET /api/tenant/payments/{id}` | Every event on the payment, including gateway notifications whether verified or not. |

Each audit entry has the time, the actor (type, id, name), IP, action, target and details.

## The hash chain

Each entry stores the hash of the entry before it and its own hash over both. Changing or deleting any entry breaks every hash after it. **Verify** in the console (or `?verify=true`) walks the whole chain and says whether it's intact or where it breaks.

This makes tampering evident, not impossible: someone with write access to the database could rewrite the whole chain. Ship the logs elsewhere (the APIs make that easy) if you need an independent copy.
