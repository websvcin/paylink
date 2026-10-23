---
sidebar_position: 1
title: Security model
---

# Security model

## Cards never touch PayLink

Payers enter card and bank details on the gateway's own page (Razorpay, PayU, Stripe…), never on PayLink. PayLink sends them there and hears back. That keeps a PayLink installation out of most PCI DSS scope: it never stores, processes or transmits card numbers. PayLink UPI moves no money at all: the payer pays the merchant's UPI ID directly.

## Sign-in and admission

PayLink stores no passwords. Every person signs in through LoginLink (OpenID Connect, with PKCE), and PayLink checks on every request that they're still admitted and with which role or access. Removing someone in LoginLink, or their role, takes effect at their next sign-in; removing them in PayLink takes effect at once.

## Keys

| Key | Stored as | Shown |
|---|---|---|
| `sk_`, `tdk_`, `pdk_` | SHA-256 hash | Once, when created |
| `whsec_` (webhook signing) | Encrypted | Once, when created |

Keys have prefixes so you (and secret scanners) can tell what a key is at a glance; the tenant slug in a key names whose it is and isn't secret, the random part is. Developer and platform keys carry only the capabilities ticked, can expire, and can be revoked at once. A key can never give a new key more than it has, and platform keys can only be created from a signed-in admin session.

## Secrets at rest

Database passwords, LoginLink client secrets, gateway credentials, SMTP and FileLink passwords are encrypted with AES-256-GCM, with a key kept in the data folder (or `PayLink__MasterKey`). They are never shown again in full: forms show them masked, and APIs never return them.

## Gateways

Every notification from a gateway is stored, verified or not, and only a verified one changes a payment: HMAC signatures (Razorpay, Stripe), SHA-512 reverse hashes (PayU), decryption with the merchant's key (CCAvenue), PayPal's own verification, or asking the gateway directly when the return isn't signed (PhonePe, Stripe returns). Statuses only move forward, so a forged or late message can't undo a payment.

## Webhooks out

Signed with HMAC-SHA256 and a timestamp (`PayLink-Signature`), sent only to public addresses by default (no SSRF into the platform's network), live endpoints `https` only.

## Payer pages

No login, no cookies needed, unguessable payment ids. UPI screenshots are shown only to the merchant's signed-in team, and every view is audited.

## Audit

Every platform and tenant action is in a [hash-chained audit log](audit-logs.md); every payment has an append-only journey.

## Reporting a vulnerability

Please report security issues privately to the maintainers through GitHub's security advisories on [websvcin/paylink](https://github.com/websvcin/paylink/security), not in public issues.
