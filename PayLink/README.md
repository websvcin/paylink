# PayLink

> **One integration for your app. Every payment method becomes a setting.**
>
> A self-hosted, multi-tenant payments platform in the same family as [LoginLink](https://github.com/websvcin/loginlink) and [CrudApi](https://github.com/websvcin/crudapi). A merchant integrates once; turning on Razorpay, PhonePe, PayPal or the free PayLink UPI method is a console setting, not code.

## Status

**Phase 1 (this release): the payment core.** A merchant integrates once with the Payments API, sends payers to the hosted checkout, and takes real UPI payments with no gateway contract. Gateways (Razorpay, PhonePe, PayU, CCAvenue) arrive as connector packages in Phase 2.

| Works today | Coming next |
|---|---|
| **Payments API** (`/api/v1`): create, get, list, cancel; `Idempotency-Key`; test and live keys (`sk_test_…`, `sk_live_…`). See [docs/payments-api.md](../docs/payments-api.md) | Razorpay, PhonePe, PayU, CCAvenue connectors, refunds, status polling (Phase 2) |
| **Hosted checkout** at `/pay/{id}`: methods the merchant turned on, retry with another method, return to the merchant | Connector catalog: install, allow, drain from Admin (Phase 3) |
| **PayLink UPI** (free): `upi://pay` QR code to the merchant's UPI ID, payer's UTR and screenshot, merchant's verification queue, reused-UTR check | Risk score, location and VPN rules, funnel and failure reports, alerts, settlement reconciliation (Phase 3) |
| **Simulator** for Test: Succeed, Fail or Stay pending, no gateway account needed | Payment links, JS drop-in button, PayPal and Stripe, UPI statement matching (Phase 4) |
| Payment journey: append-only event log per payment (who, when, from where, why), statuses that only move forward | Subdomain per tenant, PostgreSQL (later) |
| Signed webhooks (`PayLink-Signature`) with retries for 24 hours, delivery log and **Send again** | |
| Risk rules: block lists (email, domain, phone, customer, IP/CIDR), amount limits, attempts per IP, review queue | |
| Tenant developer keys (`tdk_…`) and the tenant developer API; API request log | |
| Console: Test/Live switch, overview, payments search, payment page, UPI verification, payment methods, developers, risk rules | |
| Everything from Phase 0: setup wizard, SQLite/MySQL platform and tenant databases, auto-provisioning, LoginLink sign-in and admission, platform developer keys, hash-chained audit logs | |

## Run it

```bash
docker run -d -p 8080:8080 -v $(pwd)/App_Data:/app/App_Data --name paylink websvcin/paylink:latest
docker logs paylink        # shows the one-time setup code (also in App_Data/setup-code.txt)
```

Open `http://localhost:8080/setup`. Or, from this folder, `docker compose up -d` (add `--profile mysql` for a MySQL server next to it; see `docker-compose.yml`). To build the image yourself, run `docker build -f PayLink/Dockerfile -t websvcin/paylink .` from the repository root, because it also needs `packages/` and `scripts/`.

### What setup asks for

1. **Data folder and setup code.** The page shows where data is kept and how to move it (`PayLink__DataRoot`). In Docker it warns when `/app/App_Data` isn't a mounted volume. The setup code stops a stranger from claiming a fresh server: it's in the startup log and in `App_Data/setup-code.txt`, it's filled in for you when you open setup on the server itself (`localhost`), and you can choose it yourself with `PayLink__SetupCode`.
2. **Auto-provisioning** (optional): a MySQL server, Plesk (login and password, or a secret key) or cPanel PayLink may create databases on, for the platform database and, if you allow it, for tenants (approved LoginLink organizations, everyone, or none). In a container, host fields offer the Docker host's address (e.g. `172.17.0.1`) instead of `localhost`. Change it later in Admin → Database hosting and Admin → Auto-provisioning.
3. **Platform database**: SQLite (default), a MySQL database you run (fields or a connection string), or one created through auto-provisioning with a name you can edit. An existing PayLink platform database is reattached. Engines this build doesn't have (PostgreSQL today) are listed but greyed out; they come from the storage package, not from the pages.
4. **LoginLink** (required):
   - the server address and the **platform organization** whose people may use `/admin` (any existing organization, or blank for LoginLink's platform root),
   - PayLink's public address (sign-in returns to `{address}/auth/callback`),
   - a LoginLink **management credential** with *Register apps* (PayLink registers its "PayLink admin" app itself), or the client ID and secret of an app you registered,
   - optionally a LoginLink **Platform API credential** with *CreateTenants*, so tenants can create a new organization at signup.
5. **First admin**: sign in with LoginLink. You become the first platform admin.

Admin → Settings chooses which database options tenants see at signup (SQLite, their own MySQL, auto-provisioned MySQL).

Back up `App_Data/keys`. `master.key` encrypts every stored secret (connection strings, client secrets); without it they can't be read.

## How tenants are created

At `/signup` (or by a platform admin at **Admin → Tenants → New tenant**, or through the API):

1. **LoginLink organization**: connect an existing one (slug + a management credential with *Register apps* and *Manage APIs*) or create a new one. PayLink registers a "PayLink console" app in it and asks the person to sign in as one of its admins.
2. **Name and address**: the console lives at `/{slug}/console`.
3. **Where the data lives**:
   - **SQLite, managed by PayLink**: nothing to set up; recommended to start.
   - **Create a MySQL database for me**: only if the platform has hosting and the organization is on the approval list (or the platform allows everyone).
   - **Use my own MySQL**: fields or a connection string; must be empty; tested before anything is created.

If any step fails, everything created for that attempt is removed.

## Project layout

```
paylink/                            repository root
├─ PayLink/                         the app (this folder)
│  ├─ PayLink.sln                   app + tests
│  ├─ PayLink.csproj, Program.cs    setup, /admin, /signup, /{slug}/console, /api/admin, /healthz
│  ├─ Api/ Identity/ Infrastructure/ Pages/ Platform/ Tenancy/ wwwroot/
│  ├─ db/                           SQL scripts, applied automatically; see db/README.md
│  │  ├─ mysql/{platform,tenant}/   mysql/provisioning/create-paylink-database.sql for DBAs
│  │  └─ sqlite/{platform,tenant}/
│  ├─ PayLink.Tests/                end-to-end tests with a fake LoginLink, on SQLite and MySQL
│  └─ Dockerfile, Dockerfile.dockerignore, docker-compose.yml, nuget.config
├─ packages/                        WebSvc.PayLink.* NuGet packages (to move to websvcin/paylink-packages)
│  ├─ WebSvc.PayLink.Abstractions   connector contract
│  ├─ WebSvc.PayLink.Storage        SQLite/MySQL, migrations, probes, MySQL/Plesk/cPanel provisioning
│  └─ WebSvc.PayLink.Storage.Tests
├─ docs/platform-api.md             the Platform Developer API
├─ scripts/pack-packages.sh         builds the packages into artifacts/packages
└─ legacy/                          old Api/ and Web/, to be deleted once their gateway code is ported
```

## Develop

```bash
cd PayLink
dotnet run                                 # http://localhost:5000, data in PayLink/App_Data
dotnet test PayLink.sln                    # SQLite tests
PAYLINK_TEST_MYSQL_ADMIN="Server=127.0.0.1;Uid=root;Pwd=…" dotnet test PayLink.sln   # adds the MySQL journey
```

The app uses the `WebSvc.PayLink.*` packages from a local feed, `../artifacts/packages`. **The first build fills it automatically** by packing `../packages`. After you change anything in `packages/`, repack so the app picks it up:

```bash
bash scripts/pack-packages.sh                                         # macOS / Linux, from the repository root
powershell -ExecutionPolicy Bypass -File scripts\pack-packages.ps1   # Windows
```

For local testing against a LoginLink on `http://localhost`, plain HTTP is accepted for loopback addresses only. Set `PayLink__LoginLink__AllowHttp=true` to allow it elsewhere (not for production).

| Setting | Meaning |
|---|---|
| `PayLink__DataRoot` | Data folder (default `App_Data` next to the app; `/app/App_Data` in Docker) |
| `PayLink__LoginLink__DefaultServer` | LoginLink address setup fills in (default `https://loginlink.websvc.in`; editable on the page) |
| `PayLink__Webhooks__AllowPrivateNetworks` | `true` lets webhooks reach private and local addresses (default: public addresses only) |
| `PayLink__Jobs__IntervalSeconds` | How often expiry and webhook delivery run (default 10; 0 turns the background loop off) |
| `PayLink__SetupCode` | Choose the setup code instead of a generated one |
| `PayLink__MasterKey` | Base64 32-byte key instead of `keys/master.key` |
| `PayLink__TrustedProxies` | Comma-separated proxy IPs whose `X-Forwarded-For`/`-Proto` are trusted |

## Security notes

- The previous code committed real secrets (database password, JWT key, gateway keys, a Google service-account key). They are removed from the files in `legacy/` but **remain in git history: rotate all of them**.
- PayLink stores no passwords. LoginLink handles sign-in; PayLink checks admission on every request.
- Stored secrets are AES-256-GCM encrypted. Developer keys are stored as SHA-256 hashes and shown once.
