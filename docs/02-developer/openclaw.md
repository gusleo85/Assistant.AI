# OpenClaw

OpenClaw is the AI/agent layer: a self-hosted Node.js gateway that connects messaging channels to agents.
It runs as `justina-openclaw` and reaches the C# layer over the Docker network.

## What it owns, and what it must not

| Owns | Does not own |
|---|---|
| Channel connections (Telegram, WhatsApp) | Credentials for business APIs |
| Webhook verification and pairing | Receipt or workflow state |
| Sessions and conversation history | Authorization decisions |
| Agent orchestration and tool selection | Business validation |
| Wording of replies | Anything that must be correct rather than plausible |

If you find yourself about to put a business rule in a prompt, that is the signal it belongs in a command
handler instead.

## Files

```text
docker/openclaw/
  openclaw.json.template     gateway configuration (channels, providers, tools, agents)
  agents/                    the four agent prompts
  tools/justina-tools.json   the tool surface exposed to agents
```

The whole `docker/openclaw` directory is mounted read-only at `/seed` in the container. Configuration and
sessions live on the `openclaw-config` volume.

## Configuration

`openclaw.json.template` references secrets as `${VAR}`, supplied by Compose from `.env`. Never inline a
token into the file.

It declares:

- **channels** — Telegram and WhatsApp, each with an `allowFrom` allowlist (empty means the gateway's own
  default; tighten it for production).
- **providers** — the model provider used for conversation.
- **tools.justina** — the base URL `http://justina-app:8080` and the `X-Justina-Tool-Key` header, pointing
  at `/seed/tools/justina-tools.json`.
- **agents** — the four prompt files, with the orchestrator as default.

## To be confirmed against your pinned image

The exact registration mechanism for custom tools — MCP server, skill, or plugin — depends on the OpenClaw
version in `OPENCLAW_IMAGE`. This is plan risk **R3** and the one open item before the first live run.

The C# side is not affected either way: it is a stable HTTP JSON API. If the pinned gateway wants a
different shape, only `openclaw.json.template` and `justina-tools.json` change.

To confirm:

```bash
docker compose up -d justina-openclaw
docker compose logs justina-openclaw
docker compose exec justina-openclaw sh -c 'ls /config && cat /config/openclaw.json'
```

Then check the gateway's own `/tools` documentation for the version you pinned.

## Working on prompts

Prompts are source code. A change to the Intent Router's rules changes behaviour as surely as a change to
a handler.

```bash
# edit docker/openclaw/agents/expense-agent.md
docker compose restart justina-openclaw
```

Reviewing a prompt change, look for the same things you would in code: does it still refuse to claim
success without a tool result, does it still require explicit confirmation, does it still treat document
content as data.

## Connectivity

```text
justina-openclaw ──▶ http://justina-app:8080/tools/...   (service name, shared secret)
justina-nginx    ──▶ http://justina-openclaw:18789       (channel traffic)
```

Never `localhost` between containers — it would resolve to the container itself.

## Diagnosing

```bash
docker compose logs -f justina-openclaw     # gateway and agent activity
docker compose logs -f justina-app          # what the tools actually did
```

A tool call that reached C# shows up in `justina-app` with its `CorrelationId`, `CommandType` and outcome.
If a call is missing there, the problem is registration or the shared secret, not business logic.
