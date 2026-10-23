---
sidebar_position: 5
title: Databases
---

# Databases

PayLink has one **platform database** and one **database per tenant**. Each can be SQLite, MySQL 8.0+ or PostgreSQL 13+, independently.

| | SQLite | MySQL | PostgreSQL |
|---|---|---|---|
| Platform database | Default; a file in the data folder | A database you run, or one created by [auto-provisioning](database-auto-provisioning.md) | A database you run |
| Tenant database | Default; one file per tenant in `tenants/` | The tenant's own, or auto-provisioned | The tenant's own |
| Good for | Getting started, small and medium installations | Shared hosting, panels (Plesk, cPanel), [CrudApi](../console/data-access.md) | Teams already running PostgreSQL |

**Admin → Platform settings → Database engines in this version** lists what this build supports.

## Schema and migrations

The SQL scripts ship next to the app (`db/{sqlite,mysql,postgresql}/{platform,tenant}/`) and are applied automatically at startup, in order, each once. PayLink records which have run. A tenant whose database can't be migrated at startup is listed on the admin dashboard and keeps its last schema until it's fixed.

For a DBA who prefers to create the MySQL database and user by hand, `db/mysql/provisioning/create-paylink-database.sql` has the statements.

## Connection details

Wherever a database is asked for, you can give fields (host, port, database, user, password, SSL mode) or a full connection string. The password is stored encrypted and shown masked afterwards. Every connection is tested before it's saved: a tenant's own database must be empty.

## Tenant database options at signup

**Admin → Platform settings → Tenant database options** chooses what businesses see at `/signup`:

| Option | |
|---|---|
| **SQLite, managed by PayLink** | A file in the data folder. Easiest to start with. |
| **Their own MySQL or PostgreSQL** | The tenant enters a connection to an empty database they run. |
| **Auto-provisioned MySQL** | Created on the platform's hosting, for organizations the [auto-provisioning rules](database-auto-provisioning.md#who-may-use-it) allow. |

Platform admins can always use any option from **Tenants → New tenant**.

## Moving later

A tenant's data, or the platform database, can move between engines at any time. See [backup and moving](backup-and-moving.md).
