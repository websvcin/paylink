---
sidebar_position: 4
title: PhonePe
---

# PhonePe

UPI, cards and net banking through PhonePe Payment Gateway. Two API versions: **v2** (Standard Checkout, client id and secret) for accounts created since 2024, **v1** (merchant id and salt key) for older ones.

## Settings

| Field | |
|---|---|
| **API** | `v2` or `v1`. |
| **Client ID (v2)** | PhonePe Business dashboard → Developer Settings. |
| **Client secret (v2)** | Stored encrypted. |
| **Client version (v2)** | Shown next to the client id, usually `1`. |
| **Webhook username / password (v2)** | The username and password you set for the webhook URL (shown in PayLink) in PhonePe's dashboard. |
| **Merchant ID (v1)** | |
| **Salt key (v1)** | Stored encrypted. |
| **Salt index (v1)** | Usually `1`. |

## How it's confirmed

PhonePe's redirect isn't signed, so PayLink asks PhonePe for the order status; its callback (v2: hashed username and password; v1: `X-VERIFY`) confirms it too. Refunds full and partial.
