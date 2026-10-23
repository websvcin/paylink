---
sidebar_position: 9
title: Platform settings
---

# Platform settings

**Admin → Platform settings.** Every change is in the platform audit log.

## Tenant signup

- **Open**: anyone who is an admin of a LoginLink organization can create a PayLink account at `/signup`.
- **Closed**: only platform admins create tenants (**Tenants → New tenant**, or the [platform API](../automation/platform-api.md)). `/signup` says so.

## Public address

PayLink's public address, e.g. `https://pay.example.com`. LoginLink sends people back to `{public address}/auth/callback`, and `checkout_url`s, payment link URLs and webhook URLs for gateways use it. If you change it, update the redirect URI of every PayLink app in LoginLink too.

## Tenant database options

Which database choices new tenants see at `/signup`: SQLite, their own MySQL or PostgreSQL, auto-provisioned MySQL. See [databases](databases.md#tenant-database-options-at-signup).

## Database engines in this version

Read-only: which engines this build can use for the platform and tenant databases.

## Email (SMTP)

Optional. Used for tenants' [alerts](../console/alerts.md). Server, port, from address, user, password (stored encrypted, shown masked), TLS on or off, and **Send a test** to an address you choose.

## Payer pages

Checkout and payment link pages show only the merchant and the payment. A small "Secured by PayLink" line sits under them; turn it off to offer PayLink under your own brand.

## CrudApi

The [CrudApi](https://websvcin.github.io/crudapi/) server tenants are pointed at from **Console → Data access**. Leave it blank for `https://crud.webapis.in`, or enter your own CrudApi.

## File storage

Where payers' UPI screenshots are kept:

- **The data folder** (`files/`), included in backups. Default.
- **FileLink**: a FileLink server and key, checked before it's saved.

Each stored file remembers where it is, so switching never strands old ones.

## Tenant subdomains

A tenant domain such as `paylink.example.com` lets tenants' consoles live at `acme.paylink.example.com`. See [domains and subdomains](domains-and-subdomains.md).

## Move the platform database

To SQLite, auto-provisioned MySQL, or a MySQL or PostgreSQL database you give. See [backup and moving](backup-and-moving.md#moving-the-platform-database).

## Platform limits

Floors every tenant works within; tenants can tighten them with their own risk rules, never loosen them:

- **Largest payment (₹)**: payments above it are refused.
- **Currencies allowed**: e.g. `INR, USD`.

Per-connector limits are in the [connector catalog](connector-catalog.md).

## Data folder and backups

Where the data folder is, and **Download backup**: a `.zip` of every SQLite database (copied consistently while PayLink runs) and the uploaded files. Every download is audited. See [backup and moving](backup-and-moving.md).
