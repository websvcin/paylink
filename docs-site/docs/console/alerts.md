---
sidebar_position: 11
title: Alerts
---

# Alerts

Hear about trouble on **live** payments without watching the console. Each alert fires at most once an hour, as an `alert.triggered` webhook to your live endpoints and an email to this account's owners and admins (when the platform has email set up).

| Alert when | Value | Window |
|---|---|---|
| Success rate below | % | minutes |
| A UPI payment waiting longer than | minutes | |
| A refund above | ₹ | |
| Risk blocks more than | count in the window | minutes |
| Gateway errors more than | count in the window | minutes |

Send by **webhook and email**, **webhook only** or **email only**. The list shows each alert and when it last fired; **Recent alerts** shows the messages sent.

The webhook's `data` has `kind`, `threshold`, `window_minutes` and a readable `message`.

Scripts: `GET/POST /api/tenant/alerts`, `DELETE /api/tenant/alerts/{id}`.
