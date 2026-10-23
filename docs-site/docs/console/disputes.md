---
sidebar_position: 14
title: Disputes
---

# Disputes

When a payer asks their bank or card issuer to take a payment back, the gateway tells PayLink (Razorpay, Stripe and PayPal report disputes). The dispute appears here, on the payment, and as `dispute.created`, `dispute.updated` and `dispute.closed` webhooks.

Each shows the payment, the gateway's reference, amount, reason, status and the date to respond by:

| Status | |
|---|---|
| `needs_response` | Respond with your evidence on the gateway's dashboard before the date shown. |
| `under_review` | The bank is deciding. |
| `won`, `lost`, `closed` | Final. |

PayLink follows the outcome; it doesn't submit evidence. Keep a **note** of what you sent.

Scripts: `GET /api/tenant/disputes`, `POST /api/tenant/disputes/{id}/note`; the Payments API has `GET /api/v1/disputes`.
