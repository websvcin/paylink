---
sidebar_position: 4
title: Quickstart
---

# Quickstart

From an empty server to a paid test payment in about ten minutes.

## Before you start

- **Docker** on the machine that will run PayLink.
- **A LoginLink organization** for yourself, with a management credential (LoginLink Console → API Access) that has *Register apps* and *Manage connectors*. Use the hosted LoginLink at `https://loginlink.websvc.in` or [your own](https://websvcin.github.io/loginlink/). PayLink stores no passwords: every sign-in goes through LoginLink.

## 1. Run PayLink

```bash
docker run -d -p 8080:8080 -v $(pwd)/App_Data:/app/App_Data --name paylink websvcin/paylink:latest
docker logs paylink        # shows the one-time setup code (also in App_Data/setup-code.txt)
```

`App_Data` is the only thing to keep: databases, the encryption key and uploaded files all live there. See [Docker](../self-hosting/docker.md) for Compose with MySQL or PostgreSQL.

## 2. Finish setup

Open `http://localhost:8080/setup`, enter the setup code, and follow the steps (details in [first-run setup](../self-hosting/first-run-setup.md)):

1. **Start new**, and confirm you understand the key lives in the data folder.
2. **Database hosting**: skip for now.
3. **Platform database**: SQLite.
4. **LoginLink**: the server, PayLink's public address (`http://localhost:8080` while testing), your organization and its management credential, and who may open `/admin` (people you approve is the safe default). Each part has a **Test** button; **Continue** opens only after it passes.
5. **Sign in** with LoginLink. You are the first platform admin.

## 3. Create a tenant

Open `http://localhost:8080/signup` (or **Admin → Tenants → New tenant**):

1. Connect a LoginLink organization for the business (it can be a second organization of yours) or let PayLink create one.
2. Name it, e.g. *Acme Learning*, address `acme`.
3. Data: **SQLite, managed by PayLink**.

You land in the console at `/acme/console`, in **Test** mode.

## 4. Turn on a method

**Payment methods → PayLink UPI → Turn on**: your UPI ID (e.g. `acme@okhdfcbank`), the name payers see, and tick that it's a business UPI ID. Test mode also always has the **Simulator**.

## 5. Take a payment

**Developers → Integration keys → New test secret key**, copy it, then:

```bash
curl http://localhost:8080/api/v1/payments \
  -H "Authorization: Bearer sk_test_acme_…" \
  -H "Idempotency-Key: order-1" \
  -H "Content-Type: application/json" \
  -d '{ "amount": 49900, "currency": "INR", "reference": "order-1", "description": "Course fee" }'
```

Open the `checkout_url` from the answer, choose **Simulator**, then **Succeed**. **Payments** shows it as succeeded, with its journey.

## 6. Hear about it

**Developers → Webhooks → Add a test webhook endpoint** with a URL your server answers (for a quick look use a request-bin service), then **Send test**. Every payment change now arrives signed; see [webhooks](../integrate/webhooks.md).

## Next

- Turn on a [gateway](../payment-methods/overview.md) and go [live](../integrate/going-live.md).
- Put a [payment link](../console/payment-links.md) or [Pay button](../integrate/pay-button.md) on your site without writing a backend.
- Automate the console with a [developer key](../automation/tenant-api.md).
