# Docker Architecture

Everything that belongs to Justina runs in Docker, on one network, addressing peers by **service name**.
`localhost` between containers would resolve to the container itself and is never used.

## Services

| Service | Purpose | Image | Port |
|---|---|---|---|
| `justina-ngrok` | Public ingress for channel webhooks | `ngrok/ngrok:latest` | `4040` inspector, host loopback only |
| `justina-nginx` | Reverse proxy: routing, size limits, timeouts, security headers, access log | `nginx:1.27-alpine` | `80` internal |
| `justina-openclaw` | AI/agent layer: conversation, routing, agents, tool selection | `${OPENCLAW_IMAGE}` | `18789` internal |
| `justina-app` | C#/.NET execution layer and Tool API | built from `src/Justina.Api/Dockerfile` | `8080` internal |
| `justina-sqlserver` | Receipt state, conversation state, idempotency, audit | `mcr.microsoft.com/mssql/server:2022-latest` | `1433` internal |

Nothing but the ngrok inspector is published to the host. The tool API is never reachable from the
internet: NGINX answers `/tools/` with `404`, and OpenClaw reaches it over the Docker network instead.

## Why each supporting service exists

**SQL Server** — required. C# owns authoritative receipt state, idempotency must survive restarts and
webhook retries, and the audit trail must be durable. In-memory state satisfies none of that. It is also
the team's existing database platform.

**Redis — deliberately absent.** Its candidate uses here (cache, distributed lock, idempotency keys) are
served correctly by SQL Server at this scale: a unique index gives idempotency, and `sp_getapplock` gives
locking if it is ever needed. Adding Redis now would be infrastructure without a requirement. Revisit if
`justina-app` is scaled to several replicas or measurements demand it.

## Volumes

| Volume | Holds | Notes |
|---|---|---|
| `sqlserver-data` | The database | Persistent |
| `openclaw-config` | Gateway configuration and sessions | Persistent |
| `justina-media` | Downloaded user media | Short-lived; TTL-cleaned hourly, default retention 6 hours |

`justina-media` is mounted at `/var/justina/media`, outside anything the application serves. File names
are SHA-256 hashes of the channel media id, so a hostile identifier cannot escape the directory.

## Startup order

```text
justina-sqlserver  (healthy)
        ▼
justina-app        (healthy — runs EF migrations at startup)
        ▼
justina-openclaw
        ▼
justina-nginx
        ▼
justina-ngrok
```

`justina-app` waits for SQL Server to pass its health check before starting, because the first thing it
does is apply migrations.

## Health checks

| Service | Check |
|---|---|
| `justina-sqlserver` | `sqlcmd -Q "SELECT 1"` |
| `justina-app` | `GET /health/live`; `/health/ready` additionally checks the database |
| `justina-nginx` | `GET /nginx-health` — deliberately independent of upstreams |

## Configuration and secrets

All configuration arrives as environment variables from `.env`, which is never committed. `.env.example`
is the template. Compose fails fast with a message if `MSSQL_SA_PASSWORD`, `JUSTINA_TOOL_SECRET` or
`NGROK_AUTHTOKEN` is missing, rather than starting a half-configured stack.

`justina-app` reads them through the standard .NET configuration binder, so `OpenAiVision__ApiKey` in the
environment sets `OpenAiVision:ApiKey` in configuration.

## Requirements and limits

The SQL Server image is **x64 only** and wants roughly 2 GB of RAM. On an ARM64 machine, run SQL Server
outside Docker and point `ConnectionStrings__Justina` at it — nothing in the code assumes the database is
containerized.

NGINX caps request bodies at 25 MB, slightly above the application's own 20 MB media limit, so an
oversized upload is rejected at the edge rather than buffered through the whole stack.

## Getting the public URL

```bash
curl -s http://localhost:4040/api/tunnels | jq -r '.tunnels[0].public_url'
```

On the free ngrok plan this changes on every restart, and channel webhooks must be re-pointed at it. A
reserved domain avoids that.
