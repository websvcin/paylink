---
sidebar_position: 17
title: Your domain
---

# Your domain

Use a name of your own, such as `pay.yourcompany.in`, for payment links, checkout and this console. Payers then see your domain instead of the platform's.

1. Enter a subdomain you control and don't use for anything else.
2. Add the two records shown at your DNS provider:

   | Type | Name | Value |
   |---|---|---|
   | TXT | `_paylink-challenge.pay.yourcompany.in` | `paylink-verify={token}` |
   | CNAME | `pay.yourcompany.in` | the platform's host name |

3. **Verify.** Nothing changes until the TXT record is found. Keep it: PayLink checks it again if you add the domain anew.

Once verified: console `https://pay.yourcompany.in/`, payment links `https://pay.yourcompany.in/l/…`, checkout `https://pay.yourcompany.in/pay/…`, and the Payments API returns `checkout_url`s on your domain. PayLink adds the sign-in callback to your console app in LoginLink.

The platform's reverse proxy needs a TLS certificate for your domain; see [domains and subdomains](../self-hosting/domains-and-subdomains.md) if you run the platform. **Remove domain** goes back to the platform's address.

Scripts: `GET/PUT/DELETE /api/tenant/domain`, `POST /api/tenant/domain/verify`.
