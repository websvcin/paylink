# Platform Developer API

> The full, current documentation is at https://websvcin.github.io/paylink/automation/platform-api (source in `docs-site/` on `main`).

Automates the platform console from scripts. Base path `/api/admin`.

## Keys

Create a **platform developer key** in **Admin → Developer API**: name it, tick its capabilities, and optionally limit it to tenants it creates, cap how many it may create, and set an expiry. The key (`pdk_…`) is shown once; PayLink stores only its SHA-256 hash.

```
Authorization: Bearer pdk_…
```

Keys can only be created and revoked from a signed-in console session, so a leaked key can never mint itself a replacement.

| Capability | Routes |
|---|---|
| `Tenants` | `GET /tenants`, `GET /tenants/{id}`, `PATCH /tenants/{id}`, `POST /tenants` (also needs `LoginLink`) |
| `LoginLink` | Connecting a LoginLink organization when creating a tenant |
| `AutoProvisioning` | `GET/POST /auto-provisioning/approvals`, `DELETE /auto-provisioning/approvals/{organization}` |
| `Audit` | `GET /audit` |
| `Dashboard` | `GET /dashboard` |
| `TenantOperations` | Act for any tenant in reach: `PayLink-Tenant: {slug}` on `/api/tenant` (every capability) and `/api/v1` (with `PayLink-Mode: test|live`); `GET/POST /tenants/{id}/keys`, `DELETE /tenants/{id}/keys/{keyId}` |
| `DatabaseHosting`, `Settings` | Reserved for upcoming read routes |

## Errors

Same shape as LoginLink:

```json
{ "error": "insufficient_scope", "error_description": "This key doesn't have the 'Tenants' capability." }
```

| Status | `error` | Meaning |
|---|---|---|
| 400 | `invalid_request` | Missing or invalid field; the description says which |
| 401 | `invalid_token` | No key, an unknown key, or a revoked or expired one |
| 403 | `insufficient_scope` | The key lacks the capability |
| 404 | `not_found` | No such tenant (or outside this key's reach) |
| 409 | `conflict` | Already exists |
| 429 | `quota_exceeded` | The key's tenant-creation limit is used up |
| 503 | `setup_required` | Setup isn't finished |

## Tenants

```bash
# List (q searches name, address and LoginLink organization)
curl -H "Authorization: Bearer $KEY" "https://paylink.example.com/api/admin/tenants?q=acme"

# Create, connecting the tenant's existing LoginLink organization
curl -X POST -H "Authorization: Bearer $KEY" -H "Content-Type: application/json" \
  https://paylink.example.com/api/admin/tenants -d '{
    "name": "Acme Learning",
    "slug": "acme",
    "loginLink": { "organization": "acme", "clientId": "…", "clientSecret": "…" },
    "database": { "mode": "sqlite" }
  }'
```

`loginLink` is a management credential from that organization (LoginLink Console → API Access) with *Register apps* and *Manage APIs*. `database.mode` is `sqlite`, `auto` (needs database hosting) or `byo` with `"connectionString": "Server=…;Database=…;Uid=…;Pwd=…"` pointing at an empty database. Admins of the organization are let in when they first sign in to `/{slug}/console`.

```json
201
{
  "id": 3, "slug": "acme", "name": "Acme Learning", "status": "active",
  "database": { "mode": "sqlite", "engine": "Sqlite", "display": "SQLite · tenant_acme_k3f9.db" },
  "loginLink": { "organization": "acme", "issuer": "https://loginlink.websvc.in/acme", "mode": "existing", "apiRegistration": "ok" },
  "consoleUrl": "/acme/console", "createdAt": "2026-10-19T18:02:11.123Z", "createdVia": "api"
}
```

```bash
# Suspend or reactivate
curl -X PATCH -H "Authorization: Bearer $KEY" -H "Content-Type: application/json" \
  https://paylink.example.com/api/admin/tenants/3 -d '{ "status": "suspended" }'
```

## Auto-provisioning approvals

```bash
curl -H "Authorization: Bearer $KEY" https://paylink.example.com/api/admin/auto-provisioning/approvals
curl -X POST -H "Authorization: Bearer $KEY" -H "Content-Type: application/json" \
  https://paylink.example.com/api/admin/auto-provisioning/approvals -d '{ "organization": "shopnow", "note": "pilot" }'
curl -X DELETE -H "Authorization: Bearer $KEY" https://paylink.example.com/api/admin/auto-provisioning/approvals/shopnow
```

## Audit and dashboard

```bash
curl -H "Authorization: Bearer $KEY" "https://paylink.example.com/api/admin/audit?take=100&skip=0"
curl -H "Authorization: Bearer $KEY" https://paylink.example.com/api/admin/dashboard
```

Every API action is written to the audit log with the key's name as the actor.
