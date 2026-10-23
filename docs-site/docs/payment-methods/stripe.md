---
sidebar_position: 7
title: Stripe
---

# Stripe

Cards and wallets through Stripe Checkout, in any currency your Stripe account takes. Good for international payers.

## Settings

| Field | |
|---|---|
| **Secret key** | Stripe Dashboard → Developers → API keys. Test keys start with `sk_test_`, live keys with `sk_live_` (or `rk_` for a restricted key). |
| **Webhook signing secret** (optional, recommended) | Stripe Dashboard → Developers → Webhooks: add the webhook URL shown in PayLink with `checkout.session.completed`, `checkout.session.async_payment_succeeded`, `checkout.session.async_payment_failed`, `checkout.session.expired`, `refund.updated` and `charge.dispute.*`, then paste its `whsec_…`. |

## How it's confirmed

Each attempt is a Checkout Session. Stripe's return isn't signed, so PayLink asks Stripe; its webhook (`Stripe-Signature`, refused if older than 5 minutes) confirms it too. Refunds full and partial; disputes reported.
