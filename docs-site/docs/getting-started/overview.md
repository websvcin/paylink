---
sidebar_position: 1
title: What is PayLink
---

# What is PayLink

**PayLink is a self-hosted, multi-tenant payments platform. A business integrates once; every payment method after that is a setting, not code.**

A merchant's website or app makes one call, `POST /api/v1/payments`, sends the payer to the `checkout_url` it gets back, and listens for one signed webhook. Whether that payer then pays with free PayLink UPI, Razorpay, PhonePe, PayU, CCAvenue, Stripe or PayPal is decided in the merchant's console, and can change tomorrow without a deploy.

PayLink is in the same family as [LoginLink](https://websvcin.github.io/loginlink/) (sign-in) and [CrudApi](https://websvcin.github.io/crudapi/) (data APIs). It uses LoginLink for every sign-in and can hand its data to CrudApi.

## What you get

| | |
|---|---|
| **Start today with PayLink UPI** | Free, no gateway, no paperwork. Payers scan a QR code or tap "Pay with UPI app" and pay straight into the merchant's UPI ID. The merchant confirms the UTR, or uploads a bank statement and PayLink approves matching payments itself. |
| **Every gateway behind one API** | Razorpay, PhonePe, PayU, CCAvenue, Stripe and PayPal, each a connector package. Turn them on, order them, give them amount ranges, and let PayLink fail over to the next one when a gateway is down. |
| **Help getting a gateway account** | The platform can offer "Get an account" for each gateway: a partner link or a short form its team follows up. |
| **A console for the whole team** | Payments with their full journey, payment links and QR codes, UPI verification, refunds, risk rules, alerts, reports, settlement reconciliation, disputes, team roles, an audit log. Test and Live side by side. |
| **APIs for everything** | The Payments API for taking payments, an automation API that can do everything the console does, and a platform API for running PayLink itself, all described by OpenAPI. |
| **Yours to run** | Docker, SQLite to start, MySQL or PostgreSQL when you grow, each merchant in its own database if you like. Secrets encrypted at rest, keys stored as hashes, hash-chained audit logs. |

## Two kinds of people use PayLink

- **The platform operator** runs one PayLink installation for many businesses ("tenants"): an agency, a SaaS that wants payments inside its product, a hosting company, or one business with several brands. Start with the [platform admin guide](../self-hosting/platform-admin-guide.md).
- **Merchants (tenants)** sign up on that installation, turn on payment methods and take payments. Start with the [tenant console guide](../console/tenant-admin-guide.md), and give your developers [Integrate](../integrate/payments-api.md).

## Where to go next

- [Who PayLink is for](who-paylink-is-for.md): is it the right fit?
- [How it works](how-it-works.md): the moving parts in one page.
- [Quickstart](quickstart.md): from `docker run` to a paid test payment in about ten minutes.
- [API reference](../api-reference/overview.md): every route.
