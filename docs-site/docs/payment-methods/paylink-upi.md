---
sidebar_position: 2
title: PayLink UPI
---

# PayLink UPI

**Free. No gateway, no fee, no signup.** Payers pay straight into your UPI ID from any UPI app (GPay, PhonePe, Paytm, BHIM, their bank's app). Start taking payments the day you sign up.

## Settings

| Field | |
|---|---|
| **UPI ID (VPA)** | Where payers send money, e.g. `acme@okhdfcbank`. Use a merchant or business UPI ID. |
| **Name shown to the payer** | Usually your registered business name, as it appears in UPI apps. |
| **This is a business UPI ID** | Required. Collecting business payments into a personal UPI ID can break your bank's and UPI's terms. |

## The payer's side

1. At checkout the payer chooses **PayLink UPI** and sees a `upi://pay` QR code for the exact amount, with the payment ID in the note. On a phone, **Pay with UPI app** opens their UPI app directly.
2. After paying, they enter the **12-digit UTR** (UPI reference) from their app, and optionally a **screenshot**.
3. They see that the payment is being confirmed. A UTR already used on another payment is refused.

## Your side

The payment is `processing` until you decide, in **[UPI verification](../console/upi-verification.md)**: check your bank for the UTR and amount and **Approve** or **Reject**, or upload a **bank statement** and let PayLink approve every exact match. Only then does `payment.succeeded` (or `payment.failed`) go to your webhook. An alert can tell you when a UPI payment has waited too long.

Screenshots are stored in the platform's file storage, shown only to signed-in members, and every view is audited.

## Refunds

UPI can't be reversed through PayLink. Send the money back from your bank or UPI app, then **Record a manual refund** with how you did it.
