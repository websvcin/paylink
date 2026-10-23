---
sidebar_position: 10
title: Managing tenants
---

# Managing tenants

A **tenant** is one business on the platform: its own LoginLink organization, its own console at `/{slug}/console`, its own database, keys, payment methods and team.

## Creating a tenant

Three ways, all ending the same:

1. **The business signs up** at `/signup` (when [signup is open](platform-settings.md#tenant-signup)). See [signing up](../console/signing-up.md).
2. **A platform admin** in **Tenants → New tenant**: business name, console address, their LoginLink organization slug and a management credential from it (*Register apps* and *Manage APIs*), and where their data lives (SQLite, an auto-provisioned MySQL database without needing an approval, or their own empty MySQL or PostgreSQL).
3. **A script or product** with `POST /api/admin/tenants` and a platform key with *Tenants* and *LoginLink*. See [platform API](../automation/platform-api.md).

PayLink then registers a **PayLink console** app in the organization (with a sign-in method turned on), registers itself as an API there, creates and migrates the database, and records the tenant. If any step fails, everything created for that attempt is removed. The organization's admins are let in when they first sign in.

## The tenants list

**Admin → Tenants** lists every tenant with its status, database and LoginLink organization, with search. **Suspend** stops a tenant at once (sign-ins, checkouts and keys); **Reactivate** undoes it.

## A tenant's page

**Admin → Tenants → tenant** shows:

- the console address, its own domain (verified or waiting), its LoginLink organization (created by PayLink or connected, and whether a management credential is stored), where its data lives, when and how it was created;
- **Account requests**: gateway accounts it asked for through [partner onboarding](partner-onboarding.md), with their status;
- **Console address**: path (`/{slug}/console`) or subdomain (`{slug}.{tenant domain}`; the path keeps working). See [domains and subdomains](domains-and-subdomains.md);
- **Move this tenant's data** to another database. See [backup and moving](backup-and-moving.md#moving-a-tenant).

Platform admins don't get into tenants' consoles: what happens inside a tenant belongs to its own team.
