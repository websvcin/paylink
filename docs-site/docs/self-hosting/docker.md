---
sidebar_position: 2
title: Docker and Compose
---

# Docker and Compose

PayLink ships as one image, `websvcin/paylink`. It listens on port **8080** and keeps everything in **`/app/App_Data`**.

## docker run

```bash
docker run -d -p 8080:8080 -v $(pwd)/App_Data:/app/App_Data --name paylink websvcin/paylink:latest
docker logs paylink        # the one-time setup code (also in App_Data/setup-code.txt)
```

Mount `App_Data` and nothing else: the SQLite databases, the encryption key for stored secrets, installed connector packages, location data and UPI screenshots are all in it. Setup warns you when `/app/App_Data` isn't a mounted volume, because a container rebuild would then lose everything.

The image runs as a non-root user and has a health check on [`/healthz`](reverse-proxy-and-health.md#health-check).

## Docker Compose

With the `docker-compose.yml` at the root of the [repository](https://github.com/websvcin/paylink):

```bash
docker compose up -d                       # PayLink alone, SQLite everywhere
docker compose logs paylink                # the setup code

MYSQL_ROOT_PASSWORD=choose-one docker compose --profile mysql up -d
# setup → Database hosting → "A MySQL server": host mysql, port 3306, user root, that password

POSTGRES_PASSWORD=choose-one docker compose --profile postgres up -d
# setup → Platform database → "a server I run", PostgreSQL, host postgres, port 5432, database paylink, user postgres
```

```yaml
services:
  paylink:
    image: websvcin/paylink:latest
    ports: ["8080:8080"]
    volumes: ["./App_Data:/app/App_Data"]
    environment:
      - PayLink__TrustedProxies=        # your reverse proxy's IP, if any
    restart: unless-stopped
```

## Building the image yourself

From the application's source (the `PayLink/` app folder with `packages/` and `scripts/` next to it), build from the source root, because the image also needs `packages/` and `scripts/`:

```bash
docker build -f PayLink/Dockerfile -t websvcin/paylink .
```

## Without Docker

PayLink is an ASP.NET Core 8 app. From the application's source:

```bash
cd PayLink
dotnet run          # http://localhost:5000, data in PayLink/App_Data
```

The first build packs the `WebSvc.PayLink.*` packages from `../packages` into a local feed (`../artifacts/packages`). After changing anything in `packages/`, run `bash scripts/pack-packages.sh` (or `scripts\pack-packages.ps1` on Windows).

## Next

[First-run setup](first-run-setup.md), then put it behind a [reverse proxy with TLS](reverse-proxy-and-health.md).
