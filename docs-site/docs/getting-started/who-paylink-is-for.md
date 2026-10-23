---
sidebar_position: 2
title: Who PayLink is for
---

# Who PayLink is for

## Good fits

- **A business that wants to start taking payments today.** PayLink UPI needs only a UPI ID: no gateway account, no KYC wait, no fee per payment. Add a card gateway later without touching your code.
- **A business that wants more than one gateway.** Razorpay for cards, PhonePe for UPI intent, PayPal for overseas buyers, in the order and amount ranges you choose, with automatic failover when one is down.
- **A software company that needs payments inside its product.** Run one PayLink for all your customers. Each gets its own console, its own keys and, if you like, its own database. Your product drives everything through the [platform API](../automation/platform-api.md) and can [act for each tenant](../automation/building-on-paylink.md).
- **An agency or hosting company** that sets up payments for many small businesses and wants one place to manage them, with [partner onboarding](../self-hosting/partner-onboarding.md) to get those businesses gateway accounts.
- **Anyone who must keep payment data on their own servers**, for data-residency or contractual reasons.

## Not a fit (yet)

- **You need to hold cards yourself.** PayLink never sees card numbers: payers enter them on the gateway's own page. That keeps you out of most PCI DSS scope, but PayLink is not a card vault.
- **You need payouts, split settlements or a wallet.** PayLink takes payments and refunds them. Money settles from each gateway to the merchant's own account; PayLink [reconciles](../console/reconciliation.md) it but doesn't move it.
- **You don't want to run a server.** PayLink is self-hosted. It is light (one container, SQLite to start) but someone has to run it.

## What PayLink is not

PayLink is not a payment gateway and is not a bank. It doesn't hold money or set fees. With PayLink UPI, money goes straight from the payer's bank to the merchant's UPI ID. With a gateway, the merchant's agreement with that gateway applies; PayLink only connects to it.
