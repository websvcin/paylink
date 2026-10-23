---
sidebar_position: 12
title: Reports
---

# Reports

Payments **created** in a range, for the current mode: today, 7 / 30 / 90 days, this month, last month, this year, or a custom **From / To** (up to a year; UTC days).

![Reports](/img/screenshots/console-reports.jpg)

- **Received**: amount and count of succeeded payments, and refunds.
- **Success rate**: succeeded of created.
- **Time to pay**: median, created → paid.
- **Payers who chose a method**: of checkouts opened.
- **Received per day**: a bar per day, with a table view.
- **Checkout funnel**: created → checkout opened → chose a method → paid.
- **By method**: attempts, succeeded (and rate), median time.
- **Why attempts didn't succeed**: failure reason × method × count.

**Download CSV** exports the range's payments (one row each, up to 50,000), and is audited.

Scripts: `GET /api/tenant/reports?range=30` or `?from=2026-10-01&to=2026-10-31`. For dashboards and BI tools, use the [reporting API](../integrate/reporting-api.md).
