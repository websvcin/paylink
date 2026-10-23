---
sidebar_position: 6
title: CCAvenue
---

# CCAvenue

UPI, cards, net banking, wallets and EMI through CCAvenue, in INR, USD, EUR, GBP, AED and SGD.

## Settings

| Field | |
|---|---|
| **Merchant ID** | CCAvenue Dashboard → Settings → API keys. |
| **Access code** | Access codes are tied to a website: register PayLink's address (or your own domain) in CCAvenue for the access code you enter here. |
| **Working key** | Stored encrypted. |

## How it's confirmed

The order is encrypted (AES-128, key = MD5 of the working key). CCAvenue posts back `encResp`, which only decrypts with your working key; the order status API covers the rest. Refunds full and partial.
