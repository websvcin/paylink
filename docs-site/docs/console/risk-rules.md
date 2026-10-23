---
sidebar_position: 10
title: Risk rules
---

# Risk rules

Rules run **when a payment is created** and again **when the payer starts paying**. **Block** stops the payment; **Hold for review** puts it `on_hold` until someone on your team releases or cancels it. Each decision is recorded on the payment with the rule that fired; the payer only sees a plain message that never names the rule. Rules apply to test and live payments.

![Risk rules](/img/screenshots/console-risk.jpg)

## Start from a common rule

One click adds it (edit the value first if you like):

| Rule | Check | Default | Then |
|---|---|---|---|
| Hold large payments | Amount above (₹) | 50000 | Hold for review |
| Hold risky checkouts | Risk score above | 60 | Hold for review |
| Block throwaway emails | Email domain | mailinator.com | Block |
| Slow down card testing | Attempts from one IP per hour, more than | 10 | Block |
| Block Tor | Payer uses Tor | yes | Block |
| Only accept payers in India | Payer's country is not one of | IN | Hold for review |

## Build your own

Read as a sentence: *When* (check) *is* (value), *then* (hold it for review / block it), with an optional note for your team.

| Check | Runs | Value, e.g. |
|---|---|---|
| Email address | on create | `rahul@example.com` |
| Email domain | on create | `mailinator.com` |
| Phone number | on create | `+91 98123 45678` |
| Your customer ID | on create | `cus_1234` |
| Amount above (₹) | on create | `50000` |
| Amount below (₹) | on create | `1` |
| IP address or range | at checkout | `203.0.113.7` or `203.0.113.0/24` |
| Attempts from one IP per hour, more than | at checkout | `10` |
| Payer's country is one of | at checkout | `CN, RU` (needs [location data](../self-hosting/location-data.md)) |
| Payer's country is not one of | at checkout | `IN` (needs location data) |
| Payer uses Tor | at checkout | no value |
| Indian phone, but paying from abroad | at checkout | no value |
| Risk score above (0–100) | at checkout | `70` |

Rules that need the country are skipped when it's unknown.

## The risk score

At checkout PayLink adds up these signals (0–100):

| Signal | Points |
|---|---|
| First payment from this browser | +10 |
| Each failed attempt by this payer or IP in the last 24 hours (up to 3) | +10 each |
| Throwaway email domain | +25 |
| Indian phone number, IP abroad | +20 |
| Tor exit node | +40 |
| Over 5× this merchant's usual payment | +15 |
| More than 5 attempts from this IP in an hour | +15 |

Start with **Hold for review above 60**, watch what it catches for a week, then tighten. The score and its signals are on the payment's journey.

## Platform limits

The platform's [limits](../self-hosting/platform-settings.md#platform-limits) (largest payment, currencies) apply first; your rules can only tighten them.

Scripts: `GET/POST /api/tenant/risk-rules`, `DELETE /api/tenant/risk-rules/{id}`.
