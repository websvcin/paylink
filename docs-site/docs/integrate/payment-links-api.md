---
sidebar_position: 6
title: Payment links API
---

# Payment links API

A payment link is a reusable page at `/l/{code}`: payers see what it's for, enter the amount if you left it open, and go to checkout. Every press of **Pay** creates an ordinary payment with `link_id` set, so webhooks and reports work as usual. Make them in [Console → Payment links](../console/payment-links.md) or here, with a secret key (test keys make test links, live keys live ones).

```bash
curl https://pay.example.com/api/v1/links \
  -H "Authorization: Bearer sk_test_acme_…" -H "Content-Type: application/json" \
  -d '{ "title": "Term fees, Class 6", "amount": 149900, "code": "term-fees", "ask_email": true, "max_payments": 1 }'
```

| Field | |
|---|---|
| `title` | Required. Shown to the payer and used as the payment's description. |
| `amount` | A fixed amount in minor units. Leave it out to let the payer choose, optionally within `min_amount` and `max_amount`. |
| `code` | Optional, `a-z`, `0-9` and `-`, unique across the platform. Generated if left out. A taken code answers `409 code_taken`. |
| `ask_email`, `ask_phone` | Ask the payer for these (required when asked). |
| `max_payments` | Stop after this many succeeded payments; `1` makes a one-time link. |
| `expires_at` | ISO 8601 time after which the link closes. |
| `return_url`, `description`, `currency` | Optional. |

Answers `201` with the link, including its `url`, `open` (taking payments now), `payments_succeeded` and `amount_collected`.

| Method | Path | |
|---|---|---|
| `GET` | `/api/v1/links` | All links for the key's mode. |
| `GET` | `/api/v1/links/{id or code}` | One link. |
| `POST` | `/api/v1/links/{id}/deactivate` | Stop taking payments. |
| `POST` | `/api/v1/links/{id}/activate` | Take payments again. |
| `GET` | `/api/v1/payments?link_id=lnk_…` | Payments made through a link. |

The automation API has the same with search, paging and QR codes: `/api/tenant/links`.
