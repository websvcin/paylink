---
sidebar_position: 6
title: Database auto-provisioning
---

# Database auto-provisioning

PayLink can **create** MySQL databases on demand: for the platform database at setup, for a tenant at signup, or when moving a tenant. Each database gets its own user and generated password, stored encrypted. New databases are named `paylink_{address}_{random}`.

Set it up in setup step 2 or **Admin → Database hosting**.

## Where databases are created

| Kind | What PayLink needs | Notes |
|---|---|---|
| **A MySQL server** | Host, port, an admin user and password that may create databases and users, SSL mode, and which hosts new users may connect from (`%` or PayLink's address). | Works with any MySQL 8.0+ you run, including the `mysql` profile of the Compose file. |
| **Plesk** | Panel host or IP and port, then either the admin **login and password** or a **secret key**, and the **subscription ID** the databases belong to. | Uses the Plesk REST API (`/api/v2/databases`, `/api/v2/dbusers`). |
| **cPanel** | cPanel host and port, the **account user name** and an **API token**. | Uses cPanel UAPI. cPanel requires names to start with the account name and `_`; PayLink adds it. |

**Host PayLink connects to (optional).** Panels often report their database server as `localhost`. If PayLink runs elsewhere (another machine, or a container), give the address PayLink should use. In Docker the common answers are offered as suggestions: the Docker gateway PayLink detects, `172.17.0.1`, `host.docker.internal`.

**Test & verify** creates a throwaway database and user, connects as that user, and removes both. If the panel won't remove them, the message says exactly what was left behind so you can delete it.

## Who may use it

**Admin → Auto-provisioning** decides who gets an auto-provisioned database at signup:

| Mode | |
|---|---|
| **Off** | Nobody at signup. Platform admins can still create one from **Tenants → New tenant**. |
| **Approved LoginLink organizations only** (default) | Add an organization's slug before they sign up. They prove who they are by signing in through LoginLink as its admin, so no email check is needed. Each approval shows whether it has been used. |
| **Everyone** | Any new tenant. Each database is created on your hosting account, with its limits and costs. |

The same list is available to scripts: `GET/POST /api/admin/auto-provisioning/approvals`, `DELETE /api/admin/auto-provisioning/approvals/{organization}`.

If anything fails during signup, everything created for that attempt (database, user, LoginLink app) is removed.
