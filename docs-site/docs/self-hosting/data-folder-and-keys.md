---
sidebar_position: 4
title: Data folder and encryption key
---

# Data folder and encryption key

Everything PayLink needs to keep is in one folder: `App_Data` next to the app, `/app/App_Data` in Docker, or wherever `PayLink__DataRoot` points.

| Path | What it is |
|---|---|
| `bootstrap.db` | Where the platform database is, the hosting and LoginLink settings needed to start, and (unless `PayLink__MasterKey` is set) the encryption key. |
| `platform.db` | The platform database when it's SQLite: tenants, platform admins, settings, platform keys, connector catalog, partner programmes, payment routes, audit log. |
| `tenants/` | Each SQLite tenant's own database (`tenant_{slug}_….db`): payments, attempts, refunds, links, keys, webhooks, risk rules, alerts, settlements, members, audit log. |
| `files/` | Payers' UPI screenshots (when file storage is the data folder). |
| `connectors/` | Connector packages installed from nuget.org or uploaded. |
| `geo/` | The country database and the Tor exit list from **Location data**. |
| `setup-code.txt` | The setup code, until setup is finished. |

Tenants on MySQL or PostgreSQL keep their payment data in their own database; the platform keeps only an encrypted connection to it.

## The encryption key

Secrets PayLink has to use later (database passwords, LoginLink client secrets, gateway keys, SMTP and FileLink passwords, webhook signing secrets) are encrypted with AES-256-GCM. The key is kept inside `bootstrap.db`, so a copy of the data folder is a complete backup.

Keys PayLink only has to *check* (`sk_`, `tdk_`, `pdk_` keys) are never stored at all: only their SHA-256 hash.

If you set `PayLink__MasterKey` to a base64 32-byte key, that key is used instead and isn't in the folder. Back it up separately: without it, stored secrets can't be read.

## Permissions

The folder holds every tenant's data and secrets. Keep it readable only by the PayLink process, back it up encrypted, and don't serve it from a web server.
