# PayLink

> **One integration for your app. Every payment method becomes a setting.**
>
> A self-hosted, multi-tenant payments platform. Start today with free PayLink UPI, add Razorpay, PhonePe, PayU,
> CCAvenue, Stripe or PayPal when you're ready, and run it all from one console and one API.

📖 **[Documentation](https://websvcin.github.io/paylink/)** · 🧩 Family: [LoginLink](https://websvcin.github.io/loginlink/) · [CrudApi](https://websvcin.github.io/crudapi/)

---

## What you get

- **PayLink UPI, free** — payers pay straight into your UPI ID; approve UTRs or upload a bank statement and let PayLink match them
- **Every gateway behind one API** — Razorpay, PhonePe, PayU, CCAvenue, Stripe, PayPal; ordering, amount ranges and automatic failover
- **Help getting a gateway account** — partner links or a form per gateway, tracked per merchant
- **A console for the whole team** — payments and their journeys, payment links and QR codes, refunds, risk rules, alerts, reports, settlement reconciliation, disputes, roles, audit log
- **APIs for everything** — the Payments API, an automation API that does everything the console does, and a platform API; OpenAPI and Swagger built in
- **Yours to run** — Docker, SQLite to start, MySQL or PostgreSQL per merchant, secrets encrypted, sign-in through LoginLink

See the [full documentation](https://websvcin.github.io/paylink/) for guides, API references and the self-hosting guide.

## Run it

```bash
docker run -d \
  -p 8080:8080 \
  -v $(pwd)/App_Data:/app/App_Data \
  --name paylink \
  websvcin/paylink:latest
docker logs paylink        # the one-time setup code
```

Then open `http://localhost:8080/setup`. Or use the `docker-compose.yml` in this repository (`--profile mysql` or `--profile postgres` adds a database server).

See the [quickstart](https://websvcin.github.io/paylink/getting-started/quickstart) and the [self-hosting guide](https://websvcin.github.io/paylink/self-hosting/platform-admin-guide) for setup, reverse proxy and database options.

## This repository

The `main` branch holds the documentation site (`docs-site/`, published to GitHub Pages by `.github/workflows/deploy-docs.yml`) and this README. The application's source is developed on its own branch.

## Support

This is a self-hosted product — for questions or issues, reach out to whoever operates your PayLink deployment.
