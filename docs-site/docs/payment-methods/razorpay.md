---
sidebar_position: 3
title: Razorpay
---

# Razorpay

Cards, UPI, net banking and wallets through your Razorpay account. Each attempt is a Razorpay Payment Link.

## Settings

| Field | Where |
|---|---|
| **Key ID** | Razorpay Dashboard → Account & Settings → API keys. Test keys start with `rzp_test_`, live keys with `rzp_live_`. |
| **Key secret** | The same page. Stored encrypted. |
| **Webhook secret** (optional, recommended) | Razorpay Dashboard → Webhooks: add the webhook URL shown in PayLink with the events `payment_link.paid`, `payment_link.expired`, `payment_link.cancelled`, `refund.processed`, `refund.failed` and `payment.dispute.*`, and the same secret. |

## How it's confirmed

The payer returns with a signed query string (checked), Razorpay's webhook (`X-Razorpay-Signature`) confirms it, and PayLink asks Razorpay if neither arrives. Refunds full and partial; disputes reported.

## Reconciliation

Razorpay's settlement report has the payment id (`pay_…`), amount, fee and tax: upload it in [Reconciliation](../console/reconciliation.md).
