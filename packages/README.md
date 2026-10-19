# WebSvc.PayLink packages

Libraries PayLink uses through `PackageReference`, each with its own version. They live here for now and are meant to move to **github.com/websvcin/paylink-packages** and **nuget.org**.

| Package | What it is |
|---|---|
| `WebSvc.PayLink.Abstractions` | The connector contract every payment connector depends on: manifest, config schema, attempts, next actions, normalized statuses and failure reasons. Kept small; a breaking change means a new major version. |
| `WebSvc.PayLink.Storage` | Engine-neutral storage: SQLite and MySQL connections, SQL-script migrations with checksums, connection probes, and database provisioning through a MySQL server, Plesk or cPanel. |

Payment connectors (`WebSvc.PayLink.Connector.Razorpay`, `.PhonePe`, …) will be added here, one package each, depending only on Abstractions.

## Build and test

```bash
dotnet test PayLink.Packages.sln
PAYLINK_TEST_MYSQL_ADMIN="Server=127.0.0.1;Uid=root;Pwd=…" dotnet test PayLink.Packages.sln   # also creates and drops a real MySQL database
bash ../scripts/pack-packages.sh    # packs into ../artifacts/packages for the app to use
```

## Publishing (once on nuget.org)

Bump `<Version>` in the package's `.csproj`, then:

```bash
dotnet pack src/WebSvc.PayLink.Storage -c Release -o out
dotnet nuget push out/WebSvc.PayLink.Storage.*.nupkg --source https://api.nuget.org/v3/index.json --api-key $NUGET_API_KEY
```

After publishing, remove the `paylink-local` source from the app's `nuget.config`.
