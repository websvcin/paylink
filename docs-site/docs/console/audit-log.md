---
sidebar_position: 19
title: Audit log
---

# Audit log

Every action in this console and through your keys: sign-ins, team changes, method settings, keys, refunds, UPI decisions, exports, screenshot views, risk rules, domain changes. Each entry has the time, the actor (a person, a key by name, a platform key acting for you, or the system), their IP, the action, its target and details.

Entries are **hash-chained**: each entry's hash covers the one before it, so a changed or deleted entry breaks the chain. **Verify** checks the whole chain. See [audit logs](../security/audit-logs.md).

Payments have their own, more detailed record: the [journey](payments.md#one-payment).

Scripts: `GET /api/tenant/audit?verify=true`.
