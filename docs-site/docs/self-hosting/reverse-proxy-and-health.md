---
sidebar_position: 16
title: Reverse proxy, TLS and health
---

# Reverse proxy, TLS and health

Run PayLink behind a reverse proxy that terminates TLS (Caddy, nginx, Traefik, a load balancer). Live webhooks, gateways' return URLs and LoginLink all expect `https`.

## Trusted proxies

Set `PayLink__TrustedProxies` to your proxy's IP address(es), comma-separated. PayLink then reads the client's IP and `https` from `X-Forwarded-For` and `X-Forwarded-Proto`. Without it, every request looks like it came from the proxy, and risk rules by IP or country can't work.

## What must reach PayLink

| Path | From |
|---|---|
| `/pay/…`, `/l/…`, `/js/paylink.js` | Payers' browsers |
| `/hooks/{tenant}/{connector}/{test|live}` | Gateways (webhooks) |
| `/pay/{id}/return` | Payers coming back from gateways (GET or POST) |
| `/auth/callback` | Browsers returning from LoginLink |
| `/api/…` | Merchants' servers and scripts |
| `/admin`, `/{slug}/console`, `/signup`, `/setup`, `/docs` | People |

PayLink must be able to reach LoginLink, the gateways' APIs and the merchants' webhook endpoints. Webhooks go to public addresses only, unless `PayLink__Webhooks__AllowPrivateNetworks=true`.

## Health check

`GET /healthz`:

```json
{ "status": "ok", "setupComplete": true, "platformDatabase": "ok", "degradedTenants": [], "version": "0.4.0" }
```

It answers `200` when healthy (or before setup), `503` with `"status": "degraded"` when the platform database can't be reached. `degradedTenants` lists tenants whose database couldn't be migrated at startup. The Docker image's health check uses it.

## Background jobs

One loop in the app, for every active tenant, expires unpaid payments, asks gateways about open attempts and pending refunds, evaluates alerts, and delivers and retries webhooks, every `PayLink__Jobs__IntervalSeconds` (default 10; `0` turns it off, e.g. on a second replica).
