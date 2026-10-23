---
sidebar_position: 4
title: Building a product on PayLink
---

# Building a product on PayLink

Say you run a school-management SaaS and want every school to take fees online. Run one PayLink, and let your product drive it with **one platform key**: no per-school key to store, and everything a school's admin could do in the console, your product can do for them.

## 1. One platform key

In **Admin → Developer API**, create a key with **Tenants**, **LoginLink** and **Act for tenants**. Optionally set its reach to "only tenants this key creates" and a creation limit.

## 2. Create a tenant per customer

```bash
curl -X POST https://pay.example.com/api/admin/tenants -H "Authorization: Bearer pdk_…" -H "Content-Type: application/json" -d '{
  "name": "Sunrise School", "slug": "sunrise",
  "loginLink": { "organization": "sunrise", "clientId": "…", "clientSecret": "…" },
  "database": { "mode": "sqlite" } }'
```

The school's LoginLink admins can sign in to `/sunrise/console` whenever they like; your product doesn't have to send them there.

## 3. Act for the tenant

Add **`PayLink-Tenant: {slug}`** to any call:

```bash
# Tenant API, with every capability: turn on PayLink UPI
curl -X PUT https://pay.example.com/api/tenant/methods/upi/live \
  -H "Authorization: Bearer pdk_…" -H "PayLink-Tenant: sunrise" -H "Content-Type: application/json" \
  -d '{ "enabled": true, "settings": { "vpa": "sunrise@okicici", "payee_name": "Sunrise School", "business_vpa_confirmed": "true" } }'

# Payments API, as the tenant's secret key would (PayLink-Mode: test or live; default test)
curl -X POST https://pay.example.com/api/v1/payments \
  -H "Authorization: Bearer pdk_…" -H "PayLink-Tenant: sunrise" -H "PayLink-Mode: live" \
  -H "Idempotency-Key: fee-2026-T2-1042" -H "Content-Type: application/json" \
  -d '{ "amount": 1500000, "reference": "fee-2026-T2-1042", "return_url": "https://sunrise.example.com/fees/1042" }'

# Webhooks for that tenant, pointing at your product
curl -X POST https://pay.example.com/api/tenant/webhooks \
  -H "Authorization: Bearer pdk_…" -H "PayLink-Tenant: sunrise" -H "Content-Type: application/json" \
  -d '{ "url": "https://api.example.com/paylink/sunrise", "livemode": true }'
```

| | On `/api/tenant` | On `/api/v1` |
|---|---|---|
| The platform key acts as | a developer key with **every** capability | the tenant's **secret** key, in `PayLink-Mode` (`test` by default) |

## Or hand out tenant keys

If parts of your system should hold a narrower key, mint one per tenant with `POST /api/admin/tenants/{id}/keys` (an `sk_` key, or a `tdk_` key with chosen capabilities), and revoke it with `DELETE`.

## Safety

- The key needs the **TenantOperations** capability; without it, `403 insufficient_scope`.
- Reach is enforced: a key limited to its own tenants gets `401` for any other slug.
- Suspended tenants answer `403 tenant_suspended`.
- Every call is in the **tenant's** audit log as "platform key {name} (pdk #{id})", and key creation also in the platform's.
- Revoking the platform key in **Admin → Developer API** stops all of it at once.
