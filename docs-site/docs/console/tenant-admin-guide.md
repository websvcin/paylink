---
sidebar_position: 1
title: Tenant console guide
---

# Tenant console guide

Your console is at `/{slug}/console` (or your [subdomain or own domain](your-domain.md)). Your team signs in through your LoginLink organization.

## Test and Live

The switch at the top of the menu flips the whole console between **Test** and **Live**. A yellow bar reminds you when you're in Test. Payments, links, keys, webhook endpoints and method settings are separate for each; risk rules and the team are shared. See [test and live](../getting-started/core-concepts.md#test-and-live).

## The menu

| Page | For |
|---|---|
| [Overview](overview.md) | The last 7 days, set-up checklist, account and sign-in details. |
| [Payments](payments.md) | Every payment with filters, date ranges and CSV; one payment's journey, refunds, holds. Create a payment without code. |
| [Payment links](payment-links.md) | Shareable payment pages, QR codes, a Pay button. |
| [UPI verification](upi-verification.md) | Approve or reject PayLink UPI payments, or match a bank statement. History of decisions. |
| [Reports](reports.md) | Received per day, checkout funnel, success by method, failure reasons, CSV. |
| [Disputes](disputes.md) | Chargebacks from Razorpay, Stripe and PayPal. |
| [Reconciliation](reconciliation.md) | Gateway settlement reports matched to payments; daily close. |
| [Risk rules](risk-rules.md) | Block or hold payments by amount, email, IP, country, risk score… |
| [Alerts](alerts.md) | Webhook and email alerts on live trouble. |
| [Audit log](audit-log.md) | Everything anyone did here. |
| [Payment methods](payment-methods.md) | Turn methods on, routing and failover, [get a gateway account](getting-a-gateway-account.md). |
| [Data access](data-access.md) | Reporting API and CrudApi. |
| [Your domain](your-domain.md) | Use `pay.yourcompany.in`. |
| [Developers](developers.md) | Integration keys, automation keys, webhooks, API request log. |
| [Team & sign-in](team-and-sign-in.md) | Who may sign in, roles, the role map. |

## Who can do what

| | owner | admin | developer | finance | viewer |
|---|---|---|---|---|---|
| See everything | ✓ | ✓ | ✓ | ✓ | ✓ |
| Create, cancel, release, refund payments; UPI decisions; links; reconciliation; dispute notes | ✓ | ✓ | | ✓ | |
| Payment methods, routing, risk rules, alerts, domain, data access | ✓ | ✓ | | | |
| Keys, webhooks, API request log | ✓ | ✓ | ✓ | | |
| Team, admission, role map | ✓ | ✓ | | | |
| Make someone an owner | ✓ | | | | |

Everything you can do here can also be scripted with an [automation key](../automation/tenant-api.md).
