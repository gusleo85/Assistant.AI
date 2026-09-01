# Docker

Architecture and rationale: [../01-architecture/docker-architecture.md](../01-architecture/docker-architecture.md).
This page is operational.

## Commands

```bash
docker compose up --build          # build and start everything
docker compose up -d justina-sqlserver   # just the database, for local dotnet run
docker compose ps                  # status and health
docker compose logs -f justina-app
docker compose restart justina-openclaw  # after editing an agent prompt
docker compose config >/dev/null   # validate compose + .env substitution
docker compose down                # stop, keep data
docker compose down -v             # stop and delete the database
```

## The application image

`src/Justina.Api/Dockerfile`, two stages.

**Build.** Project files are copied and restored *before* the source, so editing a `.cs` file does not
invalidate the package layer. If you add a project, add its `.csproj` copy line — otherwise restore fails
inside the image while working fine locally.

**Runtime.** `mcr.microsoft.com/dotnet/aspnet:10.0` plus:

- `libfontconfig1` and `libfreetype6` — PDFium and SkiaSharp need them to rasterize PDF pages. Without
  them, rendering fails at run time with a native load error.
- `/var/justina/media`, owned by `app` — untrusted media, on its own volume, outside anything served.
- `USER app` — the service does not run as root.

Listens on `8080`, published to no host port. Nothing but the ngrok inspector is reachable from the host.

## Networking

One network, `justina-network`. Containers address each other by **service name**:

```text
http://justina-app:8080          from OpenClaw
http://justina-openclaw:18789    from NGINX
justina-sqlserver,1433           from the app
```

`localhost` between containers resolves to the container itself and is never used.

## Health and ordering

`justina-app` waits for SQL Server to be healthy, because it applies migrations on startup. NGINX's health
check hits its own `/nginx-health` rather than an upstream, so a proxy problem is distinguishable from an
application problem.

```bash
docker inspect --format='{{json .State.Health}}' justina-app | jq
```

## Volumes

| Volume | Contents | Safe to delete? |
|---|---|---|
| `sqlserver-data` | The database | No — every receipt lives here |
| `openclaw-config` | Gateway config and sessions | Loses pairings and history |
| `justina-media` | Downloaded user media | Yes — it is TTL-cleaned anyway |

## ngrok

```bash
curl -s http://localhost:4040/api/tunnels | jq -r '.tunnels[0].public_url'
```

Re-register that URL with Telegram/WhatsApp after every restart on the free plan. A reserved domain
removes the churn.

The inspector is bound to `127.0.0.1:4040` so the tunnel dashboard is not itself public.

## Resources

SQL Server wants ~2 GB of RAM and is **x64 only**. On ARM64, drop the service and point
`ConnectionStrings__Justina` at an external instance — nothing in the code assumes a containerized
database.

## Adding a service

1. Add it to `docker-compose.yml` on `justina-network` with a documented purpose and a health check.
2. Add its variables to `.env.example` with a comment on what happens if they are blank.
3. Say why it exists in `../01-architecture/docker-architecture.md` — a service without a justification is
   a service that should not be there.
