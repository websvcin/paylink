---
sidebar_position: 13
title: Reconciliation
---

# Reconciliation

Did the money from your gateways reach your bank? Upload a gateway's **settlement report** and PayLink matches each line to a payment, marks it settled with its fees, and lists amounts that differ and lines it doesn't know.

For PayLink UPI payments, use the bank statement in [UPI verification](upi-verification.md) instead: they're not settled by a gateway.

## 1. Choose the gateway and upload its report

Choose the gateway (or "Other, or several"); matching then looks only at that gateway's payments. Upload the report as CSV: from the gateway's dashboard, usually under Settlements or Reports (save Excel files as CSV first).

What PayLink needs:

- **One row per payment**, with a header row. Every gateway names its columns differently, so you confirm them in the next step.
- **A payment reference**: the gateway's payment id (Razorpay `pay_…`, PayU `mihpayid`, Stripe `pi_…`), PayLink's payment or attempt id, or the UTR. PayLink tries each.
- **The gross amount** in rupees (or paise: there's a tick box). It must equal the payment's amount, or the line is listed for you to check.
- **Optional**: fee, tax (GST), net, settlement date, settlement id. They're saved on the payment and add up in the daily close.

**Download an example** gives the shape PayLink reads best; if a report doesn't map well, copy its rows into the example's columns:

```csv
Payment ID,Amount,Fee,Tax,Net,Settled On,Settlement ID
pay_RZP8Hq2Lx9,1499.00,29.98,5.40,1463.62,2026-10-21,setl_N4x81
```

## 2. Check which column is which

PayLink guesses from the column names; correct anything wrong. Only the payment reference and the gross amount are needed.

## 3. See what matched

"*N* lines: *a* matched, *b* need a look, *c* not found in PayLink." Then:

- **Imported reports**: file, gateway, who uploaded it, lines matched / need a look / not found.
- **Need a look**: lines whose amount differs ("PayLink has ₹500.00, the report ₹450.00") or that match nothing.
- **Daily close**: per settlement day, the number of lines, gross, fees, tax and net.
- **Not settled yet**: succeeded gateway payments still unsettled after 3 days.

Scripts: `POST /api/tenant/settlements` (multipart `report` or a CSV body; columns are guessed unless you pass their numbers) and `GET /api/tenant/settlements`.
