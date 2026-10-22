# PayLink

> **One integration for your app. Every payment method becomes a setting.**
>
> A self-hosted, multi-tenant payments platform in the same family as [LoginLink](https://github.com/websvcin/loginlink) and [CrudApi](https://github.com/websvcin/crudapi). A merchant integrates once; turning on Razorpay, PhonePe, PayPal or the free PayLink UPI method is a console setting, not code.

## Status

**Phase 4 (this release): all planned phases are in.** Everything below works and is covered by tests on SQLite, MySQL and PostgreSQL. Still open: FileLink itself, which is a separate product; PayLink already speaks the [contract](../docs/filelink-contract.md) it needs.

| Area | What works |
|---|---|
| **Setup** | New or restore (backup .zip or bootstrap.db); encryption key kept in the data folder (mount `App_Data` and nothing else; `PayLink__MasterKey` stays optional); SQLite, MySQL or PostgreSQL platform and tenant databases; auto-provisioning (tested before saving); LoginLink with clear credential checks |
| **Payments API** (`/api/v1`) | Create, get, list, cancel, refund; `Idempotency-Key`; test and live keys; links; disputes. See [docs/payments-api.md](../docs/payments-api.md) |
| **Hosted checkout** at `/pay/{id}` | The merchant's methods in their order, retry with another method, return to the merchant; a test bar in Test |
| **Gateways** | Razorpay (Payment Links), PhonePe (v2 OAuth or v1 salt key), PayU (hashed form), CCAvenue (encrypted form), Stripe (Checkout), PayPal (Orders, captured on return); signed callbacks and webhooks; status polling; full and partial refunds; disputes from Razorpay, Stripe and PayPal |
| **PayLink UPI** (free) | QR to the merchant's UPI ID, payer's UTR and screenshot, verification queue, reused-UTR check, manual refunds; bank statement matching (matching UTR and amount approve automatically) |
| **Simulator** | Test payments without a gateway |
| **Payment links and Pay button** | `/l/{code}` (fixed or payer-chosen amount, one-time or limited, QR code); `js/paylink.js` opens checkout in a window and tells your page the result |
| **Routing** | Method order and amount ranges per mode, a circuit breaker that rests a gateway after repeated errors, automatic failover to the next gateway |
| **Risk** | Rules by amount, velocity, country (block/allow), Tor, phone country vs. location, and a 0–100 score; review holds. Location from an optional MaxMind-format database (Admin → Location data) |
| **Insight** | Reports (funnel, success rate, failure reasons, methods, daily volume, CSV); alerts by webhook and email; settlement reconciliation (CSV, guessed columns, mismatches, daily close); disputes |
| **Data access** | Reporting API (`/api/v1/reporting`, CrudApi's filter language); Connect CrudApi with a SELECT-only user |
| **Journey and webhooks** | Append-only payment journey, statuses that only move forward, signed webhooks with retries and redelivery |
| **Platform** | Connector catalog (install, allow, drain, uninstall; packages from nuget.org at runtime); amount and currency limits; subdomain consoles and tenants' own domains (TXT verified); move a tenant's or the platform's database between engines; live dashboard; backup download; file storage in the data folder or FileLink |
| **Developers** | Docs at `/docs` and an OpenAPI 3 reference with Swagger UI at `/docs/api` (one document each for the Payments, tenant automation and platform APIs); tenant developer keys (`tdk_`) for scripts, including UPI approval, decision history and bank statement upload (`/api/tenant/upi/…`, guide at `/docs/automation`); platform developer API; API request log; console with a Test/Live switch |

## Run it

```bash
docker run -d -p 8080:8080 -v $(pwd)/App_Data:/app/App_Data --name paylink websvcin/paylink:latest
docker logs paylink        # shows the one-time setup code (also in App_Data/setup-code.txt)
```

Open `http://localhost:8080/setup`. Or, from this folder, `docker compose up -d` (add `--profile mysql` or `--profile postgres` for a database server next to it; see `docker-compose.yml`). To build the image yourself, run `docker build -f PayLink/Dockerfile -t websvcin/paylink .` from the repository root, because it also needs `packages/` and `scripts/`.

### What setup asks for

1. **Data folder and setup code.** The page shows where data is kept and how to move it (`PayLink__DataRoot`). In Docker it warns when `/app/App_Data` isn't a mounted volume. The setup code stops a stranger from claiming a fresh server: it's in the startup log and in `App_Data/setup-code.txt`, it's filled in for you when you open setup on the server itself (`localhost`), and you can choose it yourself with `PayLink__SetupCode`.
2. **New or restore.** The encryption key for stored secrets lives in the data folder (inside `bootstrap.db`), so mounting `App_Data` is all a container needs. Setting `PayLink__MasterKey` instead is an advanced option.
3. **Auto-provisioning** (optional): a MySQL server, Plesk (login and password, or a secret key) or cPanel PayLink may create databases on, for the platform database and, if you allow it, for tenants (approved LoginLink organizations, everyone, or none). The "Host PayLink connects to" field suggests the usual Docker addresses (`172.17.0.1`, `host.docker.internal`, or the gateway it detects). Change it later in Admin → Database hosting and Admin → Auto-provisioning.
4. **Platform database**: SQLite (default), a MySQL or PostgreSQL database you run (fields or a connection string), or a MySQL one created through auto-provisioning with a name you can edit. An existing PayLink platform database is reattached.
5. **LoginLink** (required), in four short steps: 5.1 the server and PayLink's public address; 5.2 an optional platform credential (*CreateTenants*), so PayLink can create LoginLink organizations; 5.3 your own organization: an existing one with its management credential (*Register apps*, and *Manage connectors* so PayLink can switch on a sign-in method for its app), a new one created for you, an app you registered yourself, or LoginLink's platform root; 5.4 who may open `/admin` (people you approve, the organization's LoginLink admins, or a LoginLink role). Nothing is created in LoginLink until 5.4.

Every step that takes connection details has a **Test** button, and **Continue** opens only after the test passes (changing a field closes it again). Saved secrets are shown masked (`••••••••ab12`), never in full.

6. **First admin**: sign in with LoginLink. You become the first platform admin.

Admin → Settings chooses which database options tenants see at signup (SQLite, their own MySQL, auto-provisioned MySQL).

Back up the data folder, or download a backup from **Admin → Settings → Backup** (a .zip of every SQLite database). The encryption key for stored secrets (connection strings, client secrets) lives inside `bootstrap.db`, unless you set `PayLink__MasterKey`; then keep that value safe too, since without it they can't be read. A backup restores at setup step 2.

Optional extras in the data folder: `geo/` (a MaxMind-format country database and the Tor exit list, from **Admin → Location data**), `connectors/` (connector packages installed from nuget.org), `files/` (UPI screenshots).

## How tenants are created

At `/signup` (or by a platform admin at **Admin → Tenants → New tenant**, or through the API):

1. **LoginLink organization**: connect an existing one (slug + a management credential with *Register apps* and *Manage APIs*) or create a new one. PayLink registers a "PayLink console" app in it and asks the person to sign in as one of its admins.
2. **Name and address**: the console lives at `/{slug}/console`.
3. **Where the data lives**:
   - **SQLite, managed by PayLink**: nothing to set up; recommended to start.
   - **Create a MySQL database for me**: only if the platform has hosting and the organization is on the approval list (or the platform allows everyone).
   - **Use my own MySQL**: fields or a connection string; must be empty; tested before anything is created.

If any step fails, everything created for that attempt is removed.

## Tenant addresses

A tenant's console is at `/{slug}/console` on the platform's address. Two more are optional:

- **Subdomain** (Admin → Tenants → tenant): `{slug}.{tenant domain}`, with the tenant domain set in Admin → Platform settings. Needs wildcard DNS and a wildcard certificate.
- **The tenant's own domain** (Console → Your domain): the tenant adds a TXT record `_paylink-challenge.{domain}` with the value shown, points the domain at PayLink (CNAME), and presses Verify. The console, payment links and checkout then answer there, and the Payments API returns `checkout_url`s on it. Your reverse proxy needs a certificate for each such domain. With Caddy, issue them on demand and let PayLink say which names are verified:

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

Either way, PayLink registers the new sign-in callback on the tenant's console app in LoginLink (when it holds a management credential for the organization; otherwise it says what to add).

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
└─ scripts/pack-packages.sh         builds the packages into artifacts/packages
```

## Develop

```bash
cd PayLink
dotnet run                                 # http://localhost:5000, data in PayLink/App_Data
dotnet test PayLink.sln                    # SQLite tests
PAYLINK_TEST_MYSQL_ADMIN="Server=127.0.0.1;Uid=root;Pwd=…" dotnet test PayLink.sln   # adds the MySQL journey
PAYLINK_TEST_PG_ADMIN="Host=127.0.0.1;Username=postgres;Password=…" dotnet test PayLink.sln   # every test on PostgreSQL
PAYLINK_TEST_ALL_ON_MYSQL=1 PAYLINK_TEST_MYSQL_ADMIN="…" dotnet test PayLink.sln                 # every test on MySQL
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

- The previous code committed real secrets (database password, JWT key, gateway keys, a Google service-account key). The old code (including its ResellerClub integration) has been deleted, but those secrets **remain in git history: rotate all of them**.
- PayLink stores no passwords. LoginLink handles sign-in; PayLink checks admission on every request.
- Stored secrets are AES-256-GCM encrypted. Developer keys are stored as SHA-256 hashes and shown once.
