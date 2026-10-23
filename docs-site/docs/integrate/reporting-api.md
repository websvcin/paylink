---
sidebar_position: 9
title: Reporting API
---

# Reporting API

Read-only access to a tenant's data for dashboards, spreadsheets and BI tools, with the same list parameters as [CrudApi](https://websvcin.github.io/crudapi/). Use a secret key: a test key reads test data, a live key live data.

`GET /api/v1/reporting` lists the datasets and their columns: `payments`, `attempts`, `refunds`, `disputes`, `links` and `events` (the payment journey). Secrets, IP addresses, user agents and gateway forms aren't in them.

`GET /api/v1/reporting/{dataset}` takes:

| Parameter | Example | |
|---|---|---|
| `filter` | `payments.status eq 'succeeded' and payments.amount ge 100000` | `eq ne gt ge lt le`, `and`, `or`, `not`, parentheses, `contains(…,'x')`, `startswith`, `endswith` (case-insensitive). **Qualify every column** as `dataset.column`. Text is single-quoted (`''` for a quote), numbers and `true`/`false` are bare, `eq null` / `ne null` test for empty. |
| `orderBy` | `created_at:desc,amount:asc` | |
| `display` | `id,amount,status` | Only these columns. |
| `limit`, `offset` | `limit=500` | Up to 1000 rows a page; `has_more` says whether there's another. |
| `groupBy` | `status` | One row per value with `count` and, where the dataset has one, the sum of `amount`. |
| `count` | `true` | Adds the total number of matching rows. |

```bash
curl -G https://pay.example.com/api/v1/reporting/payments -H "Authorization: Bearer sk_live_…" \
  --data-urlencode "filter=payments.created_at ge '2026-10-01' and payments.status eq 'succeeded'" \
  --data-urlencode "groupBy=connector"
```

Times are ISO 8601 text in UTC, so `ge '2026-10-01'` works. A mistake answers `400 invalid_query` with what to fix.

For every table rather than these datasets, connect [CrudApi](../console/data-access.md#crudapi-full-table-access).
