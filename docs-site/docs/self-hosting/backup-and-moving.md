---
sidebar_position: 15
title: Backup and moving
---

# Backup and moving

## Backing up

Everything PayLink needs is in the [data folder](data-folder-and-keys.md). Two ways to back it up:

- **Copy the folder** with your usual backup tool. Stop PayLink first, or use a tool that takes consistent SQLite copies.
- **Download a backup** from **Admin → Platform settings → Data folder and backups**: a `.zip` with consistent copies of every SQLite database (taken while PayLink runs) and the uploaded files. Every download is in the audit log.

Tenants on MySQL or PostgreSQL keep their payments in their own database: back those up with the database's own tools.

If you set `PayLink__MasterKey`, back that value up separately.

## Restoring

Start PayLink with an empty data folder, open `/setup`, choose **Restore**, and upload the `.zip` (or a `bootstrap.db`). PayLink reattaches the platform database and the tenants' databases.

## Moving a tenant

**Admin → Tenants → tenant → Move this tenant's data**, to:

- **SQLite, managed by PayLink**: a new file in the data folder;
- **MySQL, auto-provisioned** on the platform's database hosting;
- **a MySQL or PostgreSQL database you give**, which must be empty.

PayLink creates the new database, copies and counts every row, then switches. The tenant is paused for the copy (seconds for most tenants). The old database is kept untouched; remove it yourself once you're happy.

## Moving the platform database

**Admin → Platform settings → Move the platform database**: the same three choices. PayLink creates the new database, copies and counts every row, then switches; the old one is kept. Tenants' own databases don't move.

## Moving to another server

Stop PayLink, copy the data folder to the new server, start PayLink there with the folder mounted, and update DNS. If the public address changes, update it in **Platform settings** and the redirect URIs in LoginLink.
