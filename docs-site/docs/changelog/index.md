---
title: Changelog
---

# Changelog

## Round 6 (October 2026)

- **The automation API covers the whole console.** New tenant API routes for the overview, creating and releasing payments, filtered lists and CSV, UPI screenshots, payment links (with QR codes), reports, settlement import and close, disputes, routing, gateway account requests, webhook tests, alerts, team, domain, CrudApi, keys, the API request log and the audit log. New developer key capabilities: `links`, `reports`, `reconciliation`, `disputes`, `alerts`, `team`, `domain`, `data_access`, `keys`, `audit`, and **Everything** in the console.
- **Platform keys act for tenants.** With *Act for tenants* (`TenantOperations`), a `pdk_` key calls any tenant's `/api/tenant` and `/api/v1` with `PayLink-Tenant: {slug}`, and creates, lists and revokes tenants' keys.
- **Developers page** separates integration keys (`sk_`) from automation keys (`tdk_`).
- **This documentation site**, and a Docs link in PayLink's header.

## Round 5

- Setup: the encryption key kept in the data folder (only `App_Data` to mount), Docker address suggestions, masked secrets, Continue only after a passing test, LoginLink in four steps, a sign-in method turned on for every app PayLink registers.
- A new home page and redesigned checkout and payment link pages; the "Secured by PayLink" badge can be turned off.
- Payments filters by method, dates, amount; totals; paging; CSV of what's shown. Links search and paging. UPI decision history. Reports for any range up to a year. Bank statement and settlement report examples and guidance. One-click risk rules and a sentence-style rule builder. A platform CrudApi address. LoginLink details on the overview.
- Developer docs at `/docs`, OpenAPI with Swagger UI at `/docs/api`, UPI automation routes.
- Partner onboarding: a partner link or form per gateway, requests per tenant, a simpler Payment methods page.
- The legacy code was removed.

## Phase 4

Payment links and the Pay button; routing, circuit breaker and failover; bank statement matching for UPI; PayPal and Stripe; disputes; PostgreSQL; tenants' own domains; the reporting API; Connect CrudApi; FileLink storage.

## Phase 3

Connector catalog with packages from nuget.org; platform limits; tenant subdomains; moving databases between engines; the live admin dashboard; risk score and location rules; reports; alerts; settlement reconciliation.

## Phase 2

Razorpay, PhonePe, PayU and CCAvenue; refunds; status polling; the payer's return from gateways.

## Phase 1

Payments API, hosted checkout, PayLink UPI with verification, the Simulator, webhooks, the tenant console.

## Phase 0

Setup, LoginLink sign-in with admission, platform and tenant databases (SQLite and MySQL), tenant signup, the platform API.
