---
sidebar_position: 4
title: Idempotency and retries
---

# Idempotency and retries

Networks fail. Send an **`Idempotency-Key`** header on every `POST` that creates something (payments, refunds), so retrying can never charge twice.

- The same key with the same body returns the first result, with `200` and `Idempotent-Replayed: true`.
- The same key with a different body is `409 idempotency_conflict`.
- Use something unique per order and action, e.g. `order-1042` or `refund-1042-1`.

`reference` protects you too: creating a payment with a reference that already has an open or paid payment returns that payment.

Retry `5xx` and network errors with back-off. Don't retry `4xx` without changing the request.
