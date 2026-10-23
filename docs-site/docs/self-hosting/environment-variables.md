---
sidebar_position: 17
title: Environment variables
---

# Environment variables

Settings that must exist before the database does are environment variables (or `appsettings.json` keys with `:` instead of `__`). Everything else is in **Admin → Platform settings**.

| Variable | Default | Meaning |
|---|---|---|
| `PayLink__DataRoot` | `App_Data` next to the app; `/app/App_Data` in Docker | The data folder. |
| `PayLink__SetupCode` | generated | Choose the setup code instead of a generated one. |
| `PayLink__MasterKey` | none (key kept in `bootstrap.db`) | Base64 32-byte key for stored secrets, kept outside the data folder. |
| `PayLink__TrustedProxies` | none | Comma-separated proxy IPs whose `X-Forwarded-For` and `X-Forwarded-Proto` are trusted. |
| `PayLink__LoginLink__DefaultServer` | `https://loginlink.websvc.in` | The LoginLink address setup fills in. |
| `PayLink__LoginLink__AllowHttp` | `false` | Allow plain HTTP to LoginLink beyond loopback addresses. Not for production. |
| `PayLink__Webhooks__AllowPrivateNetworks` | `false` | Let webhooks reach private and local addresses. |
| `PayLink__Jobs__IntervalSeconds` | `10` | How often background jobs run; `0` turns them off. |
| `PayLink__Connectors__AllowedPackagePrefixes` | `WebSvc.PayLink.Connector.` | Comma-separated package id prefixes that may be installed as connectors. |
| `ASPNETCORE_HTTP_PORTS` | `8080` in Docker | The port PayLink listens on. |
