---
sidebar_position: 2
title: Signing up
---

# Signing up

Open `/signup` on the platform (if the platform admin closed signup, ask them to create your account). Your team signs in through LoginLink, so the first step is your LoginLink organization.

## 1. Your LoginLink organization

**Connect my existing LoginLink organization** (your team keeps signing in the way it already does):

1. Enter your organization's slug.
2. Enter a **management credential** from it: LoginLink Console → API Access → New credential, with *Register apps* and *Manage APIs*. It's stored encrypted.
3. **Connect and sign in.** PayLink registers a "PayLink console" app in your organization (and turns on a sign-in method for it), then asks you to sign in as one of the organization's admins.

**Or create a new LoginLink organization** (when the platform allows it): your business name, your name, email and a password for LoginLink. PayLink creates the organization and you become its first admin. Your password goes to LoginLink only.

If LoginLink says your account "has no sign-in method available for this app", open the organization in LoginLink (Console → Apps → PayLink console → Connectors), turn on Email & Password, and press **I've turned it on: continue to sign in**.

## 2. Name and address

Your business name and your console address (`/{address}/console`), e.g. `acme`. Addresses are lowercase letters, digits and hyphens, and can't be changed later.

## 3. Where your payment data lives

The choices the platform offers:

- **SQLite, managed by PayLink**: nothing to set up. Recommended to start.
- **Create a MySQL database for me**: if the platform has database hosting and your organization is approved (or the platform allows everyone).
- **Use my own MySQL or PostgreSQL**: fields or a connection string to an empty database, tested before anything is created.

If any step fails, everything created for that attempt is removed and you can try again. Your data can be moved to another database later by the platform admin.

## Next

You land in your console in **Test** mode. Follow the set-up checklist on the [overview](overview.md).
