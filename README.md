# PayLink

> **One integration for your app. Every payment method becomes a setting.**

A self-hosted, multi-tenant payments platform in the same family as [LoginLink](https://github.com/websvcin/loginlink) and [CrudApi](https://github.com/websvcin/crudapi).

| Folder | What's in it |
|---|---|
| [`PayLink/`](PayLink/README.md) | The app: code, SQL scripts (`db/`), tests, Dockerfile, docker-compose. **Start here.** |
| [`packages/`](packages/README.md) | `WebSvc.PayLink.*` NuGet packages the app uses |
| [Documentation](https://websvcin.github.io/paylink/) | The full documentation site (source in `docs-site/` on the `main` branch) |
| `scripts/` | `pack-packages.sh` / `.ps1`: packs `packages/` into the local feed the app builds from (used by the Dockerfile and CI) |

```bash
cd PayLink && docker compose up -d && docker compose logs paylink   # then open http://localhost:8080/setup
```
