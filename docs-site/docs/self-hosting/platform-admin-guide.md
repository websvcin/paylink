---
sidebar_position: 1
title: Platform admin guide
---

# Platform admin guide

You run one PayLink installation for many businesses ("tenants"). This page is the map; each section links to the details.

![The platform dashboard](/img/screenshots/admin-dashboard.jpg)

## Your first day

1. [Run PayLink](docker.md) with a mounted data folder.
2. Complete [first-run setup](first-run-setup.md): platform database, LoginLink, and your own admin sign-in.
3. Decide [who may open `/admin`](admin-access.md).
4. Set the [platform settings](platform-settings.md): public address, whether businesses may sign up themselves, which database options they get, email for alerts, limits.
5. Review the [connector catalog](connector-catalog.md): which payment methods tenants may use, per mode.
6. Optionally: [database auto-provisioning](database-auto-provisioning.md), [partner onboarding](partner-onboarding.md), [location data](location-data.md), [tenant subdomains and domains](domains-and-subdomains.md), [FileLink](platform-settings.md#file-storage).

## The platform console (`/admin`)

| Page | What it's for |
|---|---|
| **Dashboard** | Live payments in the last 7 days across tenants (received, open, UPI waiting for merchants, failed, by method, per tenant), platform counts, how the platform is set up, recent audit entries, tenants that failed to migrate at startup. |
| **Tenants** | Every tenant, search, [create one](tenants.md#creating-a-tenant), open one to suspend it, change its console address, move its data, or see its gateway account requests. |
| **Connector catalog** | [Install, allow, limit, drain and uninstall](connector-catalog.md) payment connectors, and install connector packages from nuget.org without a restart. |
| **Partner onboarding** | [Help tenants get gateway accounts](partner-onboarding.md) and track their requests. |
| **Sign-in access** | [Who may open `/admin`](admin-access.md), and approve people waiting. |
| **Developer API** | [Platform keys](../automation/platform-api.md) (`pdk_`) for scripts and products built on PayLink. |
| **Auto-provisioning** | [Which LoginLink organizations](database-auto-provisioning.md#who-may-use-it) may have a MySQL database created at signup. |
| **Database hosting** | [Where PayLink creates databases](database-auto-provisioning.md): a MySQL server, Plesk or cPanel. |
| **Location data** | A [country database and the Tor exit list](location-data.md) for tenants' risk rules. |
| **LoginLink** | The [LoginLink connection](loginlink.md) and the optional platform credential for creating organizations. |
| **Platform settings** | [Everything else](platform-settings.md): signup, public address, tenant database options, SMTP, payer page badge, CrudApi address, file storage, subdomains, moving the platform database, limits, backups. |
| **Audit log** | Every platform action, [hash-chained](../security/audit-logs.md). |

Platform admins have **admin** access (everything) or **support** access (read-only `/admin`). Platform admins don't get into tenants' consoles: those belong to each tenant's own team.

## Everyday tasks

- **A business asks for an account**: if signup is open they do it themselves at `/signup`; otherwise create it in **Tenants → New tenant** or with `POST /api/admin/tenants`.
- **A tenant is abusing the platform**: open it in **Tenants** and **Suspend**. Its team can't sign in, its checkouts say it can't take payments right now, and its keys answer `403 tenant_suspended`. Nothing is deleted; **Reactivate** undoes it.
- **A gateway is having an outage**: in the **Connector catalog**, **Drain** it. Checkouts stop offering it; payments already in flight finish normally.
- **A tenant outgrows SQLite**: **Tenants → tenant → Move this tenant's data** to MySQL or PostgreSQL. PayLink copies and counts every row, then switches.
- **Back up**: keep a copy of the data folder, or download one from **Platform settings → Backup**. See [backup and moving](backup-and-moving.md).
