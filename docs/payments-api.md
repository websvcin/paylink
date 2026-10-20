# Payments API

The one integration a merchant writes. It doesn't change when the merchant turns payment methods on or off.

1. **Create a payment** from your server: `POST /api/v1/payments`. The response has a `checkout_url`.
2. **Send the payer to it.** PayLink shows the methods you turned on, runs the method's flow, and returns the payer to your `return_url`.
3. **Trust the webhook, not the redirect.** `payment.succeeded` arrives signed with `PayLink-Signature`. You can also call `GET /api/v1/payments/{id}`.

JSON is snake_case. Amounts are integers in minor units (paise for INR: `149900` is ₹1,499.00).

## Keys

Create keys in **Console → Developers**. Every key is shown once; PayLink stores only its SHA-256 hash. Keys are created and revoked only from a signed-in console session.

| Key | Used for | Notes |
|---|---|---|
| `sk_test_{tenant}_…` | `/api/v1` in Test | Only ever creates and sees test payments. Works from day one with the built-in Simulator. |
| `sk_live_{tenant}_…` | `/api/v1` in Live | Live payments are refused (`live_not_ready`) until one payment method has Live settings that pass their check. |
| `tdk_{tenant}_…` | `/api/tenant` | Tenant developer key: automates the console within its capabilities. It can't create payments. |

```
Authorization: Bearer sk_test_acme_…
```

## Create a payment

```bash
curl https://paylink.example.com/api/v1/payments \
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
| `currency` | no | Default `INR`. Each method lists the currencies it takes. |
| `reference` | no | Your order ID. Creating another payment with the same reference while one is open or paid returns that payment instead (`200`, `Idempotent-Replayed: true`). |
| `description` | no | Shown on the checkout. Up to 300 characters. |
| `customer` | no | `name`, `email`, `phone`, `id`. Used by risk rules and search. |
| `return_url` | no | Where the payer goes after paying, with `?payment_id=…&status=…` added. |
| `metadata` | no | Up to 20 string keys, returned as-is. |
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
  "checkout_url": "https://paylink.example.com/pay/pay_8Kx2Qm4vT9abcd",
  "created_at": "2026-10-21T10:00:00.000Z",
  "expires_at": "2026-10-21T10:30:00.000Z",
  "attempts": []
}
```

**Idempotency.** Send `Idempotency-Key` on every create. The same key with the same body returns the first payment (`200`, `Idempotent-Replayed: true`); the same key with a different body is `409 idempotency_conflict`.

## Other routes

| Method | Path | |
|---|---|---|
| `GET` | `/api/v1/payments/{id}` | Status, attempts, `paid_with` (`connector`, `method`, `utr`), `failure` (`reason`, `message`). |
| `GET` | `/api/v1/payments?status=&reference=&limit=&offset=` | Newest first. |
| `POST` | `/api/v1/payments/{id}/cancel` | Open payments only. A payment that's `processing` can't be cancelled: the payer may already have paid. |

## Statuses

`created` → `requires_action` (payer chose a method) → `processing` (waiting for confirmation) → `succeeded` | `failed` | `cancelled` | `expired`.
`on_hold` means a risk rule asked for review; someone on your team releases it (back to `created`) or cancels it.

Statuses only move forward, and final ones never change: a late "failed" from a gateway can't undo `succeeded`. One step goes back on purpose: when an attempt fails, the payment returns to `requires_action` so the payer can try another method. It's still one payment, with several attempts. After 5 attempts it fails with `too_many_attempts`.

Failure reasons: `payer_cancelled`, `bank_declined`, `insufficient_funds`, `authentication_failed`, `upi_timeout`, `gateway_error`, `risk_blocked`, `rejected_by_merchant`, `too_many_attempts`, `expired`, `cancelled`.

## Webhooks

Add endpoints in **Console → Developers**, separately for Test and Live (Live needs https). Each endpoint gets a signing secret (`whsec_…`, shown once).

Events: `payment.processing`, `payment.on_hold`, `payment.succeeded`, `payment.failed`, `payment.cancelled`, `payment.expired`, and `webhook.test` from the **Send test** button.

```
POST https://acme.in/hooks/paylink
PayLink-Signature: t=1760900120,v1=5f2b9c…e41a
PayLink-Event: payment.succeeded
Content-Type: application/json

{ "id": "evt_…", "type": "payment.succeeded", "created_at": "…", "livemode": false, "data": { …the payment… } }
```

`v1` is the lowercase hex HMAC-SHA256 of `"{t}.{raw body}"` with your endpoint secret. Check it, and reject old `t` values:

```csharp
var parts = Request.Headers["PayLink-Signature"].ToString().Split(',').Select(p => p.Split('=', 2)).ToDictionary(p => p[0], p => p[1]);
using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(webhookSecret));
var expected = Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes($"{parts["t"]}.{rawBody}"))).ToLowerInvariant();
if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(expected), Encoding.ASCII.GetBytes(parts["v1"]))) return Unauthorized();
```

Answer `2xx` within 10 seconds. Anything else is retried after 1 min, 5 min, 30 min, 2 h, 6 h, 12 h and 24 h, then marked failed. Every delivery is listed on the payment and on the Developers page, with **Send again**. Handle duplicates by the event `id`.

PayLink only delivers to public addresses. Set `PayLink__Webhooks__AllowPrivateNetworks=true` to allow private ones (for a self-hosted setup where PayLink and the merchant share a network).

## Payment methods in this release

| Method | Package | How the payment is confirmed |
|---|---|---|
| **PayLink UPI** | `WebSvc.PayLink.Connector.Upi` | A `upi://pay` QR code (and "Pay with UPI app" on phones) pays straight into your UPI ID, with the payment ID in the note. The payer enters the 12-digit UTR (and optionally a screenshot); you check it in your bank or UPI app and approve it in **Console → UPI verification**. A UTR can't be reused for another payment. No gateway, no fee. |
| **Simulator** | `WebSvc.PayLink.Connector.Simulator` | Test only. The payer picks Succeed, Fail or Stay pending. |

Razorpay, PhonePe, PayU and CCAvenue arrive as their own packages in Phase 2. Gateways notify `POST /hooks/{tenant}/{connector}/{test|live}`; every notification is stored, and only a verified one changes a payment.

## Tenant developer API

Base path `/api/tenant`, `Authorization: Bearer tdk_…`. Choose test or live data with `?livemode=true|false` (default false). A missing capability returns `403 insufficient_scope`.

| Capability | Routes |
|---|---|
| `payments.read` | `GET /payments?status=&q=&limit=&offset=`, `GET /payments/{id}` (with the full journey) |
| `payments.write` | `POST /payments/{id}/cancel` `{ "note": "…" }` |
| `upi.verify` | `GET /upi/pending`, `POST /upi/{attempt_id}/approve` and `/reject` `{ "note": "…" }` (note required) |
| `methods` | `GET /methods`, `PUT /methods/{connector}/{test|live}` `{ "enabled": true, "settings": { "vpa": "acme@okhdfcbank", … } }`. Secrets are never returned. |
| `webhooks` | `GET/POST /webhooks`, `DELETE /webhooks/{id}`, `GET /webhooks/deliveries?payment_id=`, `POST /webhooks/deliveries/{id}/redeliver` |
| `risk` | `GET/POST /risk-rules`, `DELETE /risk-rules/{id}` |

## Errors

Same shape as LoginLink and the Platform Developer API:

```json
{ "error": "live_not_ready", "error_description": "Live payments need at least one payment method with Live settings that passed their check." }
```

| Status | `error` |
|---|---|
| 400 | `invalid_request`, `invalid_json`, `live_not_ready` |
| 401 | `invalid_key` |
| 403 | `wrong_key_type`, `insufficient_scope`, `tenant_suspended` |
| 404 | `not_found` |
| 409 | `idempotency_conflict`, `reference_conflict`, `final`, `processing` |

Every call is listed (without bodies) in **Console → Developers → Recent API calls**.
