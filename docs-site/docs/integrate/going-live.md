---
sidebar_position: 11
title: Going live
---

# Going live

A checklist before real money:

1. **A method with Live settings.** In **Console → Payment methods**, switch to **Live**, fill in the method's live credentials, and **Check and turn on**. Until one passes, live payments answer `live_not_ready`. PayLink UPI needs only your business UPI ID.
2. **Gateway webhooks.** Paste each gateway's **live** webhook URL from PayLink into its dashboard (they differ from the test ones: `…/live`).
3. **A live secret key** (`sk_live_…`) on your server, from **Developers** in Live mode.
4. **A live webhook endpoint** (`https`), and your server verifying `PayLink-Signature` with the live `whsec_`.
5. **Fulfil on the webhook**, not on the return URL.
6. **Risk rules** suited to you (start with [the common ones](../console/risk-rules.md#start-from-a-common-rule)) and **alerts** for success rate and UPI waiting.
7. **PayLink UPI**: someone checks UPI verification daily, or a [script uploads the bank statement](../automation/upi-automation.md).
8. Optionally, [your own domain](../console/your-domain.md) so payers see your name.

Test and Live share nothing but the team and risk rules, so test as much as you like.
