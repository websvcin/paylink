---
sidebar_position: 1
title: Overview
---

# Payment methods

Each method is a connector package. The platform [allows](../self-hosting/connector-catalog.md) it; you turn it on in **Console → Payment methods** with your own settings, separately for Test and Live; PayLink checks the settings before turning it on.

| Method | Takes | How a payment is confirmed | Refunds | Disputes |
|---|---|---|---|---|
| [PayLink UPI](paylink-upi.md) | UPI, into your own UPI ID, free | You approve the payer's UTR, or a bank statement match does | Manual (record it) | — |
| [Razorpay](razorpay.md) | UPI, cards, net banking, wallets | Signed return, signed webhook, status checks | Full and partial | ✓ |
| [PhonePe](phonepe.md) | UPI, cards, net banking | Status checks, verified callback | Full and partial | — |
| [PayU](payu.md) | UPI, cards, net banking, wallets, EMI | SHA-512 reverse hash, `verify_payment` | Full and partial | — |
| [CCAvenue](ccavenue.md) | UPI, cards, net banking, wallets, EMI | Encrypted response, order status API | Full and partial | — |
| [Stripe](stripe.md) | Cards and wallets, any currency your account takes | Status checks, signed webhook | Full and partial | ✓ |
| [PayPal](paypal.md) | PayPal, cards, pay later | Capture on return, verified webhook | Full and partial | ✓ |
| [Simulator](simulator.md) | Test only | The payer picks the outcome | Manual | — |

## How PayLink follows a gateway payment

After the payer leaves for a gateway, three things can settle the payment, whichever comes first:

1. **The payer coming back** to `/pay/{id}/return`. PayLink verifies what the gateway sent (signatures where the gateway signs it) or asks the gateway.
2. **The gateway's webhook** to `POST /hooks/{tenant}/{connector}/{test|live}`: the URL shown on the Payment methods page for you to paste into the gateway's dashboard.
3. **PayLink asking the gateway**: every minute for half an hour, then every 15 minutes.

An attempt with no result after 24 hours expires so the payer can try again; one that may already have taken money (`processing`) is never given up on. Every notification is stored, verified or not; only a verified one changes a payment.

## Refunds

A refund goes back through the gateway that took the payment, in full or in part, as many times as you like up to the payment's amount. Most gateways answer `pending` first; PayLink follows it (webhook or status checks) and sends `refund.succeeded` or `refund.failed`. The payment stays `succeeded` and `amount_refunded` grows.

PayLink UPI and the Simulator can't send money back: refund the payer yourself and record it (**Record a manual refund**, or `"manual": true` in the API), with a note saying how.

## Writing a connector

A connector implements `IPaymentConnector` from `WebSvc.PayLink.Abstractions`: a manifest (id, name, methods, currencies, capabilities, config fields), a settings check, start an attempt, handle a return or webhook, get status, refund. Package it as `WebSvc.PayLink.Connector.{Name}` and install it from the [connector catalog](../self-hosting/connector-catalog.md).
