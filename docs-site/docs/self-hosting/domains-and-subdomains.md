---
sidebar_position: 14
title: Domains and subdomains
---

# Domains and subdomains

A tenant's console is at `/{slug}/console` on the platform's address. Two more addresses are optional.

## Tenant subdomains

`{slug}.{tenant domain}`, e.g. `acme.paylink.example.com`.

1. In **Admin → Platform settings → Tenant subdomains**, set the tenant domain (`paylink.example.com`).
2. Point wildcard DNS (`*.paylink.example.com`) at PayLink and serve a wildcard TLS certificate.
3. Switch each tenant on its page (**Admin → Tenants → tenant → Console address → Subdomain**). The path address keeps working.

## A tenant's own domain

A tenant can use a name of its own, such as `pay.acme.in`, for its console, payment links and checkout (**Console → Your domain**, or `PUT /api/tenant/domain`). Payers then see the tenant's domain, and the Payments API returns `checkout_url`s on it.

1. The tenant enters the domain. PayLink shows a TXT record: name `_paylink-challenge.pay.acme.in`, value `paylink-verify={token}`.
2. The tenant adds it, and points the domain at PayLink with a CNAME to the platform's host.
3. **Verify**. Nothing changes until the TXT record is found.

Your reverse proxy needs a certificate for each such domain. With Caddy, issue them on demand and let PayLink say which names are verified:

```
{
    on_demand_tls {
        ask http://paylink:8080/.well-known/paylink/domain
    }
}
https:// {
    tls { on_demand }
    reverse_proxy paylink:8080
}
```

`GET /.well-known/paylink/domain?domain=pay.acme.in` answers `200` only for a verified domain.

## Sign-in callbacks

For either address PayLink adds the new sign-in callback to the tenant's console app in LoginLink, when it holds a management credential for the organization. Otherwise it says what to add.
