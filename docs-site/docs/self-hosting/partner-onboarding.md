---
sidebar_position: 12
title: Partner onboarding
---

# Partner onboarding

Most small businesses can start today with free PayLink UPI, but for cards and net banking they need a gateway account. Partner onboarding lets the platform help them get one, and records every request against the tenant so your team can follow up.

![Partner onboarding](/img/screenshots/admin-partners.jpg)

**Admin → Partner onboarding.**

## A programme per gateway

For each gateway (not PayLink UPI, which needs no account, nor the Simulator) choose:

| Programme | What the tenant sees and what happens |
|---|---|
| **Off** | Only "I have an account": the tenant brings its own. |
| **Partner link** | "Get an account ↗". PayLink records the tenant's interest, then opens your link (a referral or partner signup page) in a new tab. In the link, `{tenant}` is replaced by the tenant's slug so the partner can attribute the signup, e.g. `https://partner.example.com/signup?ref=yourcode&merchant={tenant}`. The link must be `https://`. |
| **Form** | "Get an account" opens a short form: contact name, email and phone (required), registered business name, website or app, expected monthly volume and a note (optional). Your team follows up. |

Give each programme a **headline** (e.g. "Get PayU in 2 minutes"; default "No {gateway} account yet? Get one through us") and an optional line under it. Avoid promising pricing or approval times: the gateway decides those.

## Requests

The **Requests** table lists every request with the tenant, gateway, how it came (opened the partner link, or filled the form), contact details, answers, and when. Filter by status and gateway. For each one set a **status** and a **note the tenant sees**:

| Status | Shown to the tenant as |
|---|---|
| `new` | Account: New |
| `in_progress` | Account: In progress |
| `done` | Account: Account ready |
| `declined` | Account: Declined |

Asking again for the same gateway while a request is open updates that request instead of adding another. A tenant's requests also show on its page in **Admin → Tenants**.

Tenants can also ask through the automation API: `GET/POST /api/tenant/account-requests`.

## Once the account is ready

The tenant enters its new credentials under **Payment methods → the gateway → I have an account** (or a script does, with `PUT /api/tenant/methods/{connector}/{mode}`), and PayLink checks them before turning the method on.
