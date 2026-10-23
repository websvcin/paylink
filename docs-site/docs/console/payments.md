---
sidebar_position: 4
title: Payments
---

# Payments

## The list

![Payments with filters and totals](/img/screenshots/console-payments.jpg)

Every payment in the current mode, newest first, whichever method was used: id and your reference, amount, status (with "refunded" or "partly refunded"), customer, method and UTR, created.

**Filters** (combine freely):

| Filter | |
|---|---|
| Status | Any status, or one. |
| Method | Any method, or one connector. |
| Created | All time, today, 7 / 30 / 90 days, this month, last month, this year, or a custom **From / To** (UTC days, both included). A quick range wins over dates still filled in. |
| Amount | Min and max in rupees. |
| Search | Exact payment id, reference, UTR, customer email, phone or id, gateway reference or attempt id; or part of the customer name or description. |

The totals card shows how many payments match, their amount and how much was paid. Pages of 50, with first/previous/next/last. **Download these as CSV** exports exactly what's shown (up to 50,000 rows); every export is in the audit log.

## Create a payment

No code needed: **Create a payment** with an amount, what it's for, your reference, and optionally the customer's email and phone. PayLink gives you a checkout link to send. It expires in 30 minutes.

## One payment

Open a payment to see:

- **Summary**: amount, status, what it's for, your reference, customer, mode, method and UTR, refunded so far, why it failed, why it's held, who created it and how (console or API), when it was paid or when it expires, its checkout link and trace id.
- **Actions**, when they apply:
  - **UPI waiting**: the payer says they paid by UPI. Check your bank for the UTR and amount, see the screenshot if they uploaded one, then **Approve: money received** or **Reject: not found** (a note is required).
  - **On hold**: a risk rule held it. **Release** (say why it's safe) lets the payer pay, or cancel it.
  - **Cancel payment**: for open payments. A payment that's `processing` can't be cancelled: the payer may already have paid.
  - **Refund**: an amount (blank refunds everything left) and a reason. Through the gateway where it supports refunds, or **I refunded it myself: record it**. For PayLink UPI and the Simulator, refund the payer from your bank or UPI app, then record it with how (e.g. "IMPS ref 1234").
- **Attempts**: each method tried, its status, gateway reference and failure.
- **Refunds** and **Disputes** for this payment.
- **Journey**: every event with time, actor (payer, gateway, your team, a key, the system), IP and what changed. Gateway notifications are kept whether verified or not; only verified ones change anything.
- **Webhooks**: every delivery for this payment, with **Send again**.

Final statuses never change. See [statuses](../integrate/statuses.md).
