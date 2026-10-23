---
sidebar_position: 3
title: How it works
---

# How it works

```
 Merchant's server ──POST /api/v1/payments──▶ PayLink ──▶ checkout_url
        ▲                                        │
        │ signed webhook                          ▼
        │                               Hosted checkout /pay/{id}
        │                                  │  payer picks a method
        └──────── payment.succeeded ◀──────┤
                                           ├─ PayLink UPI: QR → UTR → merchant approves (or bank statement)
                                           ├─ Razorpay / PhonePe / PayU / CCAvenue / Stripe / PayPal page
                                           └─ Simulator (test only)
```

1. **The merchant's server creates a payment** with a secret key: amount, reference, optional customer and `return_url`. PayLink answers with an `id` and a `checkout_url`.
2. **The payer opens the checkout.** It shows the methods the merchant turned on for that mode, in the merchant's order and only those whose amount range fits. Risk rules run first.
3. **The payer pays.** Each choice is an *attempt*. If one fails, the payer can try another method; it's still one payment.
4. **PayLink finds out** from whichever comes first: the payer returning, the gateway's signed webhook, or PayLink asking the gateway (every minute for half an hour, then every 15 minutes). For PayLink UPI, the merchant approves the UTR, or a bank statement upload approves it.
5. **The merchant hears about it** through a signed `payment.succeeded` webhook (and `return_url` for the payer). The webhook, not the redirect, is the proof.

Everything that happens is written to the payment's **journey**: an append-only list of events with who did what and when. Statuses only move forward; a late "failed" can never undo "succeeded".

## The pieces

| Piece | What it is |
|---|---|
| **Platform** | One PayLink installation, run by a platform operator at `/admin`. |
| **Tenant** | A merchant on the platform, with its own console at `/{slug}/console`, its own LoginLink organization and its own database. |
| **LoginLink** | Handles every sign-in, for platform admins and for each tenant's team. PayLink stores no passwords. |
| **Connector** | A payment method, shipped as its own NuGet package (`WebSvc.PayLink.Connector.*`). The platform installs and allows connectors; tenants turn them on with their own credentials. |
| **Payment** | One amount to collect, with a status, attempts, refunds, disputes and a journey. Test and live payments never mix. |
| **Keys** | `sk_test_`/`sk_live_` secret keys for the Payments API, `tdk_` developer keys for the automation API, `pdk_` platform keys for the platform API. |

## Three gates for a payment method

A method shows on a checkout only if it passes all three:

1. **The platform allows it** in the connector catalog (per mode, with an optional maximum amount).
2. **The tenant turned it on** for that mode, and its settings passed their check.
3. **The payment fits**: currency, the tenant's amount range for the method, and the gateway isn't resting after repeated errors (unless nothing else is left).
