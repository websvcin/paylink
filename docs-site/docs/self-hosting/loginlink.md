---
sidebar_position: 7
title: LoginLink connection
---

# LoginLink connection

PayLink stores no passwords. Every sign-in, to `/admin` and to each tenant's console, goes through [LoginLink](https://websvcin.github.io/loginlink/) with OpenID Connect. PayLink then decides who is let in (admission) on every request.

## How the pieces fit

| LoginLink | PayLink |
|---|---|
| The **platform organization** (yours, or LoginLink's platform root) | Its people sign in to `/admin`, through the **PayLink admin** app registered at setup. |
| Each **tenant's organization** | Its people sign in to `/{slug}/console`, through a **PayLink console** app registered in that organization at signup. |
| A **Platform API credential** with *CreateTenants* (optional) | Lets businesses create a new LoginLink organization during signup. |
| A **management credential** per organization | Lets PayLink register its app, register itself as an API, turn on a sign-in method, and add redirect URLs when a tenant gets a subdomain or its own domain. |

## Admin → LoginLink

Shows the server, the platform organization, the PayLink admin app, where sign-in returns (`{public address}/auth/callback`) and whether tenants can create organizations. **Check connection** tests it. Add or remove the **Platform API credential** here (create it in LoginLink at Admin → Platform Settings → Platform API Access, with *CreateTenants*).

## Sign-in methods

A LoginLink app with no sign-in method turned on can't be signed in to ("has no sign-in method available for this app"). Whenever PayLink registers an app (at setup, and for each tenant at signup) it turns one on: one that's already on, or else an approved one, preferring Password, then Magic Link, Email OTP, WebAuthn. If the credential isn't allowed to manage connectors, PayLink stops and says what to do (LoginLink Console → Apps → the app → Connectors → turn on Email & Password) instead of sending someone to a failing sign-in.

## Plain HTTP

LoginLink must be reached over HTTPS, except on loopback addresses (`localhost`, `127.0.0.1`) for local testing. `PayLink__LoginLink__AllowHttp=true` allows HTTP elsewhere; never in production.
