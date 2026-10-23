---
sidebar_position: 18
title: Upgrades
---

# Upgrades

1. **Back up** the data folder (and any MySQL or PostgreSQL databases).
2. **Pull and restart**: `docker pull websvcin/paylink:latest && docker compose up -d` (or your equivalent).
3. **Check** `/healthz` and the admin dashboard. New SQL scripts run automatically at startup, for the platform database and every tenant database; a tenant that fails is listed on the dashboard and in `degradedTenants`.

Connector packages installed from nuget.org are kept across upgrades. A connector that ships with the new version and was also installed as a package keeps using the installed package until you choose **Use the built-in version** in the connector catalog.

See the [changelog](../changelog/index.md) for what changed.
