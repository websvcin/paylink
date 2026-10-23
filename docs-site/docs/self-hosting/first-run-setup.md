---
sidebar_position: 3
title: First-run setup
---

# First-run setup

Until setup is finished every page sends you to `/setup`. Setup takes about five minutes.

## 0. Setup code

The setup code stops a stranger from claiming a fresh server. It's printed in the startup log, saved in `App_Data/setup-code.txt`, and filled in for you when you open setup on the server itself (`localhost`). Choose your own with `PayLink__SetupCode`.

The first page also shows where the data folder is and how to move it (`PayLink__DataRoot`), and warns when `/app/App_Data` isn't a mounted volume in Docker.

## 1. New or restore

- **Start new.** The encryption key for stored secrets (connection strings, client secrets, gateway keys) is created inside `bootstrap.db` in the data folder, so mounting `App_Data` is all a container needs. Tick that you understand that losing the folder loses the key.
- **Restore.** Upload a backup `.zip` from **Platform settings → Backup**, or a `bootstrap.db`. PayLink reattaches its databases.

Setting `PayLink__MasterKey` (a base64 32-byte key) keeps the key outside the folder instead. It's optional and for advanced setups; if you do it, keep that value as safe as the folder.

## 2. Database hosting (optional)

Somewhere PayLink may *create* MySQL databases: for the platform database now and, if you allow it, for tenants at signup. Choose **Off** (skip), **A MySQL server** (an admin user that can create databases and users, and which hosts new users may connect from), **Plesk** (panel host, admin login and password or a secret key, and the subscription ID) or **cPanel** (host, account user name and an API token).

**Host PayLink connects to (optional)** is the address PayLink uses for the databases it creates, when it differs from the panel's. In Docker the usual answers are offered as one-click suggestions: the gateway it detects, `172.17.0.1` or `host.docker.internal`.

**Test & verify** creates a test database and user, connects, and removes them again. If the panel refuses to remove them, the message names what was left so you can delete it. Details: [database auto-provisioning](database-auto-provisioning.md).

## 3. Platform database

- **SQLite** (default): a file in the data folder. Fine for most installations.
- **A MySQL or PostgreSQL database you run**: fields or a connection string; the test connects and checks it's empty (or already a PayLink platform database, which is reattached).
- **Create one with auto-provisioning**: a MySQL database on the hosting from step 2, with a name you can edit. Its generated user and password are shown masked.

You can [move the platform database](backup-and-moving.md#moving-the-platform-database) later.

## 4. LoginLink

Every sign-in to PayLink goes through [LoginLink](https://websvcin.github.io/loginlink/). This step has four short parts; nothing is created in LoginLink until the last one.

1. **What you need and the server.** The LoginLink address (default `https://loginlink.websvc.in`, or `PayLink__LoginLink__DefaultServer`) and **PayLink's public address**: where people reach PayLink, used for sign-in callbacks (`{public address}/auth/callback`). Test checks LoginLink answers.
2. **Platform credential (optional).** A LoginLink Platform API credential with *CreateTenants* lets businesses create a new LoginLink organization during signup. Without it they connect an organization they already have.
3. **Your organization.** Whose people sign in to `/admin`:
   - an existing organization and its **management credential** (*Register apps*, and *Manage connectors* so PayLink can switch on a sign-in method for its app),
   - a **new organization** created for you (needs the platform credential),
   - an app you **registered yourself** (client ID and secret), or
   - LoginLink's **platform root** organization.
4. **Who signs in to `/admin`.** People you approve (default), the organization's LoginLink admins automatically, or anyone with a PayLink platform role in LoginLink. See [admin access](admin-access.md).

PayLink registers a "PayLink admin" app in the organization and makes sure it has a sign-in method turned on (Password first). If it can't, it tells you exactly where to turn one on (LoginLink Console → Apps → the app → Connectors) instead of sending you to a sign-in that would fail.

## 5. First admin

Sign in with LoginLink. You become the first platform admin and land on `/admin`.

## How setup behaves

- Every step with connection details has a **Test** button. **Continue** opens only after the test passes, and closes again if you change a field.
- Nothing is saved until its test passes. Values you typed are kept, encrypted, as a draft while you move between steps.
- Saved secrets are shown masked (`••••••••••ab12`), never in full. Leave a secret field empty to keep the saved one.
