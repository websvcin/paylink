---
sidebar_position: 1
title: Payments API
---

# Payments API

The one integration a merchant writes. It doesn't change when payment methods are turned on or off.

1. **Create a payment** from your server: `POST /api/v1/payments`. The response has a `checkout_url`.
2. **Send the payer to it** (redirect, link, or the [Pay button](pay-button.md)). PayLink shows the methods you turned on, runs the method's flow, and returns the payer to your `return_url`.
3. **Trust the [webhook](webhooks.md), not the redirect.** `payment.succeeded` arrives signed with `PayLink-Signature`. You can also call `GET /api/v1/payments/{id}`.

JSON is snake_case. Amounts are integers in minor units (paise for INR: `149900` is ₹1,499.00). Base URL: your PayLink's public address (or a tenant's own domain).

## Authentication

```
Authorization: Bearer sk_test_acme_…
```

| Key | Used for |
|---|---|
| `sk_test_{tenant}_…` | Test payments only. Works from day one with the Simulator. |
| `sk_live_{tenant}_…` | Live payments. Refused with `live_not_ready` until a method has Live settings that pass their check. |

Create them in **Console → Developers → Integration keys** (or with an [automation key](../automation/tenant-api.md) with *keys*). Keep them on your server.

## Create a payment

```bash
curl https://pay.example.com/api/v1/payments \
  -H "Authorization: Bearer sk_test_acme_…" \
  -H "Idempotency-Key: ord-ACM-20931" \
  -H "Content-Type: application/json" \
  -d '{
    "amount": 149900,
    "currency": "INR",
    "reference": "ACM-20931",
    "description": "Full-stack course, monthly plan",
    "customer": { "name": "Rahul Verma", "email": "rahul@example.com", "phone": "+919812345678", "id": "cus_42" },
    "return_url": "https://acme.in/orders/ACM-20931",
    "metadata": { "plan": "monthly" },
    "expires_in": 1800
  }'
```

| Field | Required | Notes |
|---|---|---|
| `amount` | yes | Minor units, at least 1. |
| `currency` | no | Default `INR`. Each method lists the currencies it takes; the platform may limit them. |
| `reference` | no | Your order ID. Creating another payment with the same reference while one is open or paid returns that payment instead (`200`, `Idempotent-Replayed: true`). |
| `description` | no | Shown on the checkout. Up to 300 characters. |
| `customer` | no | `name`, `email`, `phone`, `id`. Used by risk rules and search. |
| `return_url` | no | Where the payer goes after paying, with `?payment_id=…&status=…` added. |
| `metadata` | no | Up to 20 string keys, returned as they are. |
| `expires_in` | no | Seconds, 60 to 604800. Default 1800 (30 minutes). |

Response `201`:

```json
{
  "id": "pay_8Kx2Qm4vT9abcd",
  "object": "payment",
  "livemode": false,
  "status": "created",
  "amount": 149900,
  "currency": "INR",
  "reference": "ACM-20931",
  "checkout_url": "https://pay.example.com/pay/pay_8Kx2Qm4vT9abcd",
  "created_at": "2026-10-21T10:00:00.000Z",
  "expires_at": "2026-10-21T10:30:00.000Z",
  "attempts": []
}
```

Risk rules run on create: a blocked payment is created already `failed` (`risk_blocked`), a held one `on_hold`. Check `status` in the response.

## Other routes

| Method | Path | |
|---|---|---|
| `GET` | `/api/v1/payments/{id}` | Status, attempts, `paid_with` (`connector`, `method`, `utr`), `failure` (`reason`, `message`), `amount_refunded`, `refunds`. |
| `GET` | `/api/v1/payments?status=&reference=&link_id=&limit=&offset=` | Newest first. |
| `POST` | `/api/v1/payments/{id}/cancel` | Open payments only. A `processing` payment can't be cancelled: the payer may already have paid. |
| `POST` | `/api/v1/payments/{id}/refunds` | See [refunds](refunds.md). |
| `GET` | `/api/v1/payments/{id}/refunds` | The payment's refunds. |
| `GET` | `/api/v1/disputes?payment_id=` | Disputes from Razorpay, Stripe and PayPal. |
| | `/api/v1/links…` | [Payment links](payment-links-api.md). |
| | `/api/v1/reporting…` | The [reporting API](reporting-api.md). |

Full reference: [Payments API reference](../api-reference/payments-api.md), or Swagger at `/docs/api` on your PayLink.

## A minimal server

```js
// Node.js (Express): create the payment, redirect, and trust only the webhook
app.post('/checkout', async (req, res) => {
  const r = await fetch('https://pay.example.com/api/v1/payments', {
    method: 'POST',
    headers: {
      Authorization: `Bearer ${process.env.PAYLINK_KEY}`,
      'Idempotency-Key': `order-${req.body.orderId}`,
      'Content-Type': 'application/json',
    },
    body: JSON.stringify({ amount: 149900, currency: 'INR', reference: `order-${req.body.orderId}`,
                           return_url: `https://acme.in/orders/${req.body.orderId}` }),
  });
  const payment = await r.json();
  res.redirect(303, payment.checkout_url);
});
```
