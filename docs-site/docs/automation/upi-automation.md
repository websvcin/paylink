---
sidebar_position: 2
title: UPI automation
---

# UPI automation

Approve PayLink UPI payments without opening the console. Create an automation key with **upi.verify**.

## 1. Upload the bank statement

Export your bank statement as CSV and send it as is (multipart field `statement`, or the raw file as `text/csv`). Every credit whose 12-digit UTR and amount match a UPI payment waiting for a check is approved; anything else stays in the queue and comes back in `need_a_look_lines`. What the file needs is described in [UPI verification](../console/upi-verification.md#match-a-bank-statement).

```bash
curl "https://pay.example.com/api/tenant/upi/statements?livemode=true" \
  -H "Authorization: Bearer tdk_acme_…" \
  -F statement=@statement.csv
```

```json
{ "upload_id": "stm_…", "credits": 14, "approved": 12, "need_a_look": 1, "not_found": 1, "without_utr": 0,
  "need_a_look_lines": [ { "line": 9, "utr": "412345678901", "amount": 49900, "narration": "UPI/412345678901/…", "note": "amount differs" } ] }
```

An unreadable file answers `422 statement_unreadable` with what's wrong.

## 2. Decide what's left

```bash
# What is waiting
curl "https://pay.example.com/api/tenant/upi/pending?livemode=true" -H "Authorization: Bearer tdk_acme_…"

# Approve one (the note is kept on the payment's journey)
curl -X POST https://pay.example.com/api/tenant/upi/att_…/approve -H "Authorization: Bearer tdk_acme_…" \
  -H "Content-Type: application/json" -d '{ "note": "Seen in HDFC statement" }'

# Reject one
curl -X POST https://pay.example.com/api/tenant/upi/att_…/reject -H "Authorization: Bearer tdk_acme_…" \
  -H "Content-Type: application/json" -d '{ "note": "No such credit" }'
```

`GET /upi/pending` gives each waiting payment's `payment_id`, `attempt_id`, amount, reference, `utr`, when it was submitted and whether there's a screenshot (`GET /payments/{id}/attempts/{attempt_id}/proof`).

## 3. Look back

```bash
curl "https://pay.example.com/api/tenant/upi/decided?livemode=true&decision=rejected&from=2026-10-01&to=2026-10-31" \
  -H "Authorization: Bearer tdk_acme_…"
```

## A nightly job

```sh
#!/bin/sh
# crontab: 30 23 * * * /opt/acme/upi-match.sh /data/statements/today.csv
set -e
curl -fsS "https://pay.example.com/api/tenant/upi/statements?livemode=true" \
  -H "Authorization: Bearer $PAYLINK_TDK" -F statement=@"$1"
```

Keep the key in an environment variable or secret store, never in the script. Every call is in the console's audit log under the key's name, and the approvals show "bank statement" as who decided.
