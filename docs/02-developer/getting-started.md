# Getting Started

## Prerequisites

| Tool | Version used | Notes |
|---|---|---|
| .NET SDK | **10.0.400** | .NET 10 is the current LTS |
| Docker | **29.3.1** | With Compose **v5.1.1** |
| `dotnet-ef` | 10.0.11 | `dotnet tool install --global dotnet-ef` |

The SQL Server container is **x64 only** and wants ~2 GB of RAM. On ARM64, run SQL Server outside Docker
and point the connection string at it.

## 1. Configure

```bash
cp .env.example .env
```

Fill in at minimum:

| Variable | Why |
|---|---|
| `MSSQL_SA_PASSWORD` | The container refuses to start without one that meets the SQL Server policy: 8+ characters, upper, lower, digit, symbol |
| `JUSTINA_TOOL_SECRET` | Shared between OpenClaw and the app. `openssl rand -hex 32` |
| `NGROK_AUTHTOKEN` | Public ingress for channel webhooks |
| `OPENAI_API_KEY` | Reading receipts |
| `TELEGRAM_BOT_TOKEN` | The first channel to bring up |

`EXPENSE_API_URL` and `EXPENSE_API_KEY` can stay blank for now — the Expense API contract has not been
supplied yet, and submission will report that the expense system is unavailable rather than failing
obscurely. See [api-integrations.md](api-integrations.md).

`.env` is git-ignored. Never commit it.

## 2. Build and test without Docker

```bash
dotnet build Justina.slnx
for p in tests/*/; do dotnet test "$p" --nologo; done
```

All tests run offline — no database, no network, no API keys. The document tests build real PDFs in
memory, and the Expense API tests run against an in-process WireMock stub.

## 3. Run the stack

```bash
docker compose up --build
```

Order is enforced: SQL Server becomes healthy, then `justina-app` starts and applies EF migrations, then
OpenClaw, NGINX and ngrok.

Check it is alive:

```bash
docker compose ps
curl -s http://localhost:4040/api/tunnels | jq -r '.tunnels[0].public_url'
```

That public URL is what you register as the webhook address with Telegram or WhatsApp. On the free ngrok
plan it changes every restart.

## 4. Grant yourself permission

Justina refuses everything for an unmapped user — that is the design, not a bug. Insert a principal with
the capabilities you need:

```sql
INSERT INTO Principals (Id, Channel, UserId, DisplayName, CapabilitiesJson)
VALUES (NEWID(), 1, '<your telegram user id>', 'Your Name',
        '["expense.submit","expense.read","recruitment.search"]');
```

`Channel` is `1` for Telegram and `2` for WhatsApp.

## 5. Try a tool call directly

The fastest way to see the backend work without any chat client:

```bash
docker compose exec justina-app sh -c '
curl -s -X POST http://localhost:8080/tools/session.context \
  -H "Content-Type: application/json" \
  -H "X-Justina-Tool-Key: $ToolApi__SharedSecret" \
  -d "{\"envelope\":{\"channel\":\"telegram\",\"userId\":\"<your id>\",\"conversationId\":\"<chat id>\"}}"'
```

You should get `{"ok":true,"data":{...capabilities...}}`. If you get `401`, the tool key is wrong; if
`503`, no shared secret is configured.

## Where next

| Task | Read |
|---|---|
| Day-to-day workflow | [local-development.md](local-development.md) |
| Where code lives | [project-structure.md](project-structure.md) |
| Why it is shaped this way | [csharp-architecture.md](csharp-architecture.md) |
| Adding a domain | [csharp-architecture.md](csharp-architecture.md#adding-a-new-domain) |
| Something is broken | [troubleshooting.md](troubleshooting.md) |
