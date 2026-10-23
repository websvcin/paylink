---
sidebar_position: 3
title: Data and privacy
---

# Data and privacy

## Where data lives

All data stays on the platform's own servers: the [data folder](../self-hosting/data-folder-and-keys.md) and the databases it points to. A tenant on its own MySQL or PostgreSQL keeps its payment data in that database, wherever it runs, which helps with data-residency requirements. PayLink sends data only to the LoginLink server, the gateways a tenant turns on, the tenant's webhook endpoints, and (if configured) the platform's SMTP server, FileLink and CrudApi.

## What PayLink keeps about payers

What the merchant sends (name, email, phone, customer id), what the payer enters on PayLink pages (email and phone on payment links, UPI UTR and screenshot), the IP address and browser of checkout events (for risk and the journey), and what gateways report (method, masked details, references). Never card numbers.

The [reporting API](../integrate/reporting-api.md) leaves out IP addresses, user agents and gateway forms.

## Retention and deletion

PayLink doesn't delete payment records by itself: merchants usually must keep them for accounting and tax. A platform operator who needs to remove a payer's personal data can do so in the tenant's database; the audit chain will show the change.
