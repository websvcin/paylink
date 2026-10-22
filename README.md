# PayLink

> **One integration for your app. Every payment method becomes a setting.**

A self-hosted, multi-tenant payments platform in the same family as [LoginLink](https://github.com/websvcin/loginlink) and [CrudApi](https://github.com/websvcin/crudapi).

| Folder | What's in it |
|---|---|
| [`PayLink/`](PayLink/README.md) | The app: code, SQL scripts (`db/`), tests, Dockerfile, docker-compose. **Start here.** |
| [`packages/`](packages/README.md) | `WebSvc.PayLink.*` NuGet packages the app uses |
| [`docs/`](docs/payments-api.md) | [Payments API](docs/payments-api.md) and [Platform Developer API](docs/platform-api.md) references |
| `scripts/` | Build helpers (`pack-packages.sh`) |

```bash
cd PayLink && docker compose up -d && docker compose logs paylink   # then open http://localhost:8080/setup
```
