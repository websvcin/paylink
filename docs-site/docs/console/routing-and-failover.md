---
sidebar_position: 8
title: Routing and failover
---

# Routing and failover

**Payment methods → Order and amounts**, separately for Test and Live, for the methods that are on.

| Setting | |
|---|---|
| **Order** | The order payers see methods in. Lower first. |
| **From (₹) / Up to (₹)** | Offer the method only for payments in this range, e.g. PayLink UPI up to ₹1,00,000 and a card gateway above it. Empty means any amount. |
| **Now** | *Healthy*, or *Resting after errors*. |

## The circuit breaker

A gateway that fails to **start** a payment 3 times in a row rests for 5 minutes: checkout leaves it out, unless no other method is left. It comes back by itself.

## Failover

**If a gateway can't start a payment, send the payer to the next gateway automatically** (on by default). The payer doesn't see an error; the next method in order is tried, and the journey records both attempts.

Scripts: `GET/PUT /api/tenant/routing`.
