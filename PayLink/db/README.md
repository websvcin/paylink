# PayLink database scripts

Plain SQL, one folder per engine and per database kind. PayLink applies these automatically. You only need them if you want to create or inspect databases by hand.

```
db/
├─ mysql/
│  ├─ platform/     0001_init.sql, 0002_payment_routes.sql   platform database (tenant registry, admins, keys, audit, payment → tenant routes)
│  ├─ tenant/       0001_init.sql, 0002_payments.sql         one database per tenant (team, sign-in policy, audit; payments, attempts, journey, keys, webhooks, risk rules)
│  └─ provisioning/ create-paylink-database.sql   create an empty database + user by hand
└─ sqlite/
   ├─ platform/     0001_init.sql …
   └─ tenant/       0001_init.sql …
```

## How PayLink uses them

- Scripts are numbered (`0001_init.sql`, `0002_…`) and applied in order. Each applied script is recorded in a `schema_migrations` table with a checksum.
- The setup wizard applies `platform/` scripts to the platform database. Creating a tenant applies `tenant/` scripts to its new database. On every start, PayLink applies any new scripts to the platform database and to each tenant database.
- **Never edit a script that has been applied.** PayLink refuses to start against a database whose recorded checksum doesn't match. Add a new numbered script instead.
- SQLite and MySQL scripts must describe the same tables. When you add `mysql/tenant/0002_x.sql`, add `sqlite/tenant/0002_x.sql` too.

## Creating a MySQL database by hand

1. Edit the placeholders in `mysql/provisioning/create-paylink-database.sql` and run it as a MySQL admin.
2. In the setup wizard (platform database) or tenant signup (**Use my own MySQL**), enter the database, user and password, or paste a connection string such as
   `Server=db.example.com;Port=3306;Database=paylink_example;Uid=paylink_user;Pwd=…;SslMode=Required`.
3. PayLink tests the connection, checks the database is empty, and creates its tables.

You can also run the scripts yourself in order with the `mysql` client. Most only use `CREATE TABLE IF NOT EXISTS` and `INSERT IGNORE`; scripts that add columns (`ALTER TABLE`, from `0003` on) can only run once, so if you ran scripts by hand, insert their rows into `schema_migrations` too (version, name, checksum, applied_at), or let PayLink apply everything itself. The one exception is a database that already has other tables: tenant signup requires an empty database.
