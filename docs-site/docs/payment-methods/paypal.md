---
sidebar_position: 8
title: PayPal
---

# PayPal

PayPal balance, cards and pay later for international payers. Test uses PayPal's sandbox.

## Settings

| Field | |
|---|---|
| **Client ID** | PayPal Developer Dashboard → Apps & Credentials. Sandbox apps for Test, live apps for Live. |
| **Secret** | Stored encrypted. |
| **Webhook ID** (optional, recommended) | In the same app, add a webhook with the URL shown in PayLink and the events `PAYMENT.CAPTURE.COMPLETED`, `PAYMENT.CAPTURE.DENIED`, `PAYMENT.CAPTURE.REFUNDED` and `CUSTOMER.DISPUTE.*`, then paste its ID. |

## How it's confirmed

Each attempt is a PayPal order. PayLink captures the approved order when the payer returns, or at its next status check if they closed the window. Webhooks are verified by PayPal itself, with your webhook ID. Refunds full and partial; disputes reported.
