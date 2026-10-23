---
sidebar_position: 3
title: Statuses and failure reasons
---

# Statuses and failure reasons

## Payment statuses

| Status | Meaning |
|---|---|
| `created` | Waiting for the payer. |
| `requires_action` | The payer chose a method and needs to act (scan, pay on the gateway's page), or an attempt failed and they can try another. |
| `processing` | The payer says they've paid; waiting for confirmation (gateway, or the merchant's UPI approval). |
| `on_hold` | A risk rule asked for review. Someone on the merchant's team releases it (back to `created`) or cancels it. |
| `succeeded` | Paid. Final. |
| `failed` | Final; see `failure.reason`. |
| `cancelled` | Cancelled by the merchant. Final. |
| `expired` | Not paid in time. Final. |

Statuses only move forward, and final ones never change: a late "failed" from a gateway can't undo `succeeded`. One step goes back on purpose: when an attempt fails, the payment returns to `requires_action` so the payer can try another method. It's still one payment, with several attempts.

A refunded payment stays `succeeded`; `amount_refunded` tells you how much went back.

## Failure reasons

| `failure.reason` | |
|---|---|
| `payer_cancelled` | The payer cancelled at the gateway. |
| `bank_declined` | The bank or card issuer declined. |
| `insufficient_funds` | |
| `authentication_failed` | OTP, 3-D Secure or UPI PIN failed. |
| `upi_timeout` | The UPI collect or intent wasn't completed in time. |
| `gateway_error` | The gateway failed. |
| `risk_blocked` | A risk rule blocked it. |
| `rejected_by_merchant` | The merchant rejected the UPI payment's UTR. |
| `too_many_attempts` | 5 attempts without success. |
| `limit_exceeded` | Over a limit set by the gateway, the platform or the method's amount range. |
| `expired`, `cancelled` | |
| `unknown` | The gateway gave no reason. |

`failure.message` has the gateway's or merchant's words when there are any.

## Refund statuses

`pending` → `succeeded` | `failed`.

## Dispute statuses

`needs_response`, `under_review`, `won`, `lost`, `closed`.
