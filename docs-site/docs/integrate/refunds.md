---
sidebar_position: 5
title: Refunds
---

# Refunds

```bash
curl https://pay.example.com/api/v1/payments/pay_8Kx2Qm4vT9abcd/refunds \
  -H "Authorization: Bearer sk_live_acme_…" \
  -H "Idempotency-Key: refund-ACM-20931-1" \
  -H "Content-Type: application/json" \
  -d '{ "amount": 50000, "reason": "Plan downgraded" }'
```

Leave out `amount` to refund everything left. You can refund several times, up to the payment's amount.

The refund goes back through the gateway that took the payment. It's returned as `pending`, `succeeded` or `failed`; most gateways answer `pending` first, then PayLink follows it and sends `refund.succeeded` or `refund.failed`. The payment stays `succeeded`; `amount_refunded` grows and `refunds` lists them.

| Error | When |
|---|---|
| `409 not_paid` | The payment isn't `succeeded`. |
| `409 refunds_not_supported` | The method can't refund through PayLink (PayLink UPI, Simulator). Refund the payer yourself and record it in the console or with the automation API (`"manual": true`, reason required). |
| `400 invalid_request` | More than what's left. |
