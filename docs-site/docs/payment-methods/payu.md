---
sidebar_position: 5
title: PayU
---

# PayU

UPI, cards, net banking, wallets and EMI through PayU. The payer is sent to PayU with a hashed form.

## Settings

| Field | |
|---|---|
| **Merchant key** | PayU Dashboard → Developers → API keys (test and live keys differ). |
| **Merchant salt (v1)** | Stored encrypted. For webhooks, add the webhook URL shown in PayLink in PayU Dashboard → Webhooks for successful, failed and refund events. |

## How it's confirmed

PayU posts the result back with a SHA-512 reverse hash, checked before anything changes; `verify_payment` covers the rest. Refunds full and partial. In settlement reports the payment reference is PayU's `mihpayid`.
