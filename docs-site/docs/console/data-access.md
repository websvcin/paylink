---
sidebar_position: 18
title: Data access
---

# Data access

Read your payment data from other tools. Nothing here can change a payment, and payments never depend on it.

## Reporting API (built in)

Payments, attempts, refunds, disputes, links and the payment journey, with filters, grouping and paging, using an integration key. Test keys read test data, live keys live data.

```bash
curl -G https://pay.example.com/api/v1/reporting/payments -H "Authorization: Bearer sk_test_…" \
  --data-urlencode "filter=payments.status eq 'succeeded'" --data-urlencode "groupBy=connector"
```

See [reporting API](../integrate/reporting-api.md).

## CrudApi (full table access)

Connects your PayLink database to a [CrudApi](https://websvcin.github.io/crudapi/) workspace, for REST and OData on every table. Read-only twice over: a database user that can only read, and CrudApi's Reader role. CrudApi reads MySQL, so this is for MySQL tenant databases; SQLite tenants are told why and pointed to the reporting API.

1. **A read-only database user.** On an auto-provisioned database, leave it blank and PayLink creates one with `SELECT` only. Otherwise enter a user you made (`GRANT SELECT ON yourdb.* TO …`); PayLink checks it can read and refuses it if it can write. Set **Host CrudApi uses** if CrudApi reaches the database by another address.
2. **Register it in CrudApi.** The CrudApi address (filled in with your platform's CrudApi), your workspace and a workspace developer key (`wdk_…`, with Databases and API credentials). PayLink registers the database and creates a Reader API client; its secret is shown once. Your tables are then at `{crudapi}/api/v1/{workspace}/{id}/crud/payments`. Or register it yourself in CrudApi with the details shown.
3. **Disconnect** unregisters it and removes the read-only user PayLink made.

Scripts: `GET/DELETE /api/tenant/data-access`, `PUT /api/tenant/data-access/reader`, `POST /api/tenant/data-access/register`.
