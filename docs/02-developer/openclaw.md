# OpenClaw

OpenClaw is the AI/agent layer: a self-hosted Node.js gateway connecting messaging channels to agents.
It runs as `justina-openclaw` and reaches the C# layer **over MCP**.

Verified against the image actually in use: **OpenClaw 2026.8.1** (`ghcr.io/openclaw/openclaw:latest`),
whose own documentation ships inside the container at `/app/docs`. This closes plan risk R3.

## What it owns, and what it must not

| Owns | Does not own |
|---|---|
| Channel connections (Telegram, WhatsApp) | Credentials for business APIs |
| Sessions and conversation history | Receipt or workflow state |
| Agent orchestration and tool selection | Authorization decisions |
| Wording of replies | Business validation |

If you are about to put a business rule in a prompt, that is the signal it belongs in a command handler.

## The three things that surprised the original design

**1. Custom tools are MCP, not HTTP.** There is no configuration for calling a plain HTTP JSON API — no
`tools.custom`, no HTTP tool type. The supported routes are `mcp.servers`, plugins (a real package plus a
gateway restart), and filesystem skills. So `justina-app` serves **MCP over Streamable HTTP at `/mcp`**;
see [tools.md](tools.md). The REST endpoints under `/tools` remain for testing.

**2. Telegram uses long polling by default.** No public URL, no webhook, **no ngrok** — only outbound
HTTPS to `api.telegram.org`. Webhook mode exists (`channels.telegram.webhookUrl` + `webhookSecret`) but is
optional. ngrok is only needed for WhatsApp, which does require a public callback.

**3. Agents have no `systemPrompt` key.** OpenClaw agents are per-workspace identities selected by
`bindings[]` on channel/account — not an intent-routing hierarchy. Persona comes from **bootstrap files in
the agent's workspace** (`AGENTS.md`, `SOUL.md`, `IDENTITY.md`), injected per `contextInjection`.

Justina therefore runs **one agent** whose `AGENTS.md` carries the orchestrator, routing rules and both
domain behaviours. The guarantees that matter — authorization, workflow state, never reaching the wrong
domain's API — are enforced in C#, not by the prompt, so collapsing four prompts into one costs nothing
structural.

## Files

```text
docker/openclaw/
  openclaw.json           gateway configuration (seeded into the state volume)
  workspace/AGENTS.md     the agent's persona and rules — review this like code
```

Both are **copied** into the `openclaw-config` volume by the `justina-openclaw-init` service, not
bind-mounted. The gateway rewrites `openclaw.json` atomically, so a read-only mount or a symlink breaks
its own writes. Existing files are never overwritten; to reapply the checked-in versions:

```bash
docker compose down -v
```

## Configuration that matters

| Key | Why |
|---|---|
| `gateway.mode: "local"` | The gateway refuses to start on anything else |
| `gateway.bind: "lan"` | The default `loopback` binds `127.0.0.1` **inside** the container and is unreachable from other services |
| `gateway.auth.mode/token` | A non-loopback bind makes auth mandatory |
| `gateway.terminal.enabled: false` | Otherwise an admin can open a host shell in the container |
| `mcp.servers.justina` | `transport: "streamable-http"` (not `type: "http"`), plus the static `X-Justina-Tool-Key` header |
| `tools.profile: "coding"` | Implicitly allows `bundle-mcp`, which is what makes the Justina tools reachable |
| `agents.defaults.workspace` | Where `AGENTS.md` is read from |
| `channels.telegram.dmPolicy` | `pairing` while the allowlist is empty — `allowlist` with no entries is rejected by validation |

Environment variables: `OPENCLAW_CONFIG_PATH` points at the **file**, `OPENCLAW_STATE_DIR` at the writable
directory. (`OPENCLAW_CONFIG_DIR`, used in an earlier draft, is not a real variable.) `${VAR}` substitution
inside the config is uppercase-only and is filled from the container environment, so no secret is written
into the file.

**Validation is strict**: an unknown key makes the gateway refuse to start. Do not invent keys.

**Ownership**: the gateway runs as uid 1000 (`node`) and writes state, backups and SQLite into the state
directory. The init service `chown`s the volume — without it the gateway dies with
`EACCES: mkdir '/config/state'`.

## Verifying it works

```bash
docker compose logs -f justina-openclaw

docker exec justina-openclaw openclaw mcp list          # justina should be listed
docker exec justina-openclaw openclaw mcp probe justina # expect: 8 tools
docker exec justina-openclaw openclaw doctor
```

A healthy startup logs `gateway ready`, then
`[default] starting provider (@YourBot)` and `isolated polling worker poll-start`.

## Working on the agent

`AGENTS.md` is source code. A change to the routing rules changes behaviour as surely as a change to a
handler.

```bash
# edit docker/openclaw/workspace/AGENTS.md, then either:
docker cp docker/openclaw/workspace/AGENTS.md justina-openclaw:/config/workspace/AGENTS.md
# or reseed from scratch:
docker compose down -v && docker compose up -d
```

Reviewing a prompt change, look for the same things you would in code: does it still refuse to claim
success without a tool result, does it still require explicit confirmation, does it still treat document
content as data.

## Connectivity

```text
justina-openclaw ──▶ http://justina-app:8080/mcp   (service name, shared secret)
justina-nginx    ──▶ http://justina-openclaw:18789 (channel traffic, WhatsApp webhooks)
```

Never `localhost` between containers — it resolves to the container itself.

## Known limitation: agent-supplied identity

MCP carries only **static** headers, so the agent passes `channel`, `userId` and `conversationId` as tool
arguments. Justina still resolves capabilities from the database rather than trusting any claim of
permission, and refuses anything belonging to another conversation — but a compromised or misled agent
could name a different user id.

The gateway's own channel policy (`dmPolicy`, `allowFrom`) is the first gate and should be kept tight.
A signed identity claim, or a per-user gateway session, would close this properly. Flagged for the
Product Owner.
