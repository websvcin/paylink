---
sidebar_position: 13
title: Location data
---

# Location data

Optional. With it, tenants' [risk rules](../console/risk-rules.md) can use the payer's **country** and **Tor**, and the risk score counts an Indian phone number paying from abroad. Location comes from the IP address, so it can be wrong behind a VPN; rules that need it are skipped when the country is unknown.

**Admin → Location data.**

## Country database

Upload a free country database in MaxMind's `.mmdb` format:

- **MaxMind GeoLite2 Country** (free account at maxmind.com), or
- **DB-IP IP to Country Lite** (db-ip.com, CC BY 4.0: credit DB-IP where you show it).

The page shows which database is installed and when it was built. Update it monthly.

## Tor exit list

**Download now** fetches Tor's public list of exit nodes. Refresh it now and then. Other VPNs and proxies aren't detected; that needs a paid database.

## Look up an address

Enter an IP address to see the country and whether it's a Tor exit, to check the data works.

Both files are kept in the data folder (`geo/`) and included in backups.
