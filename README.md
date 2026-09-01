# Justina

An AI business assistant you talk to on **WhatsApp** and **Telegram**. Send a photo or PDF of a receipt,
check what Justina read, correct anything it got wrong in plain language, confirm — and it becomes an
expense. Exactly once.

## The idea

The language model is very good at understanding what someone meant. It is not a safe place to keep money,
permissions, or workflow state. So Justina splits on exactly that line:

**The AI proposes; C# disposes.**

Everything the AI can do, it does by calling a named tool with typed arguments. There is no generic HTTP
tool, no shell, no database access. Authorization, validation, workflow state and every external API call
live in C#, where they can be tested.

## Architecture

```text
WhatsApp / Telegram
        ▼
     ngrok  ▶  NGINX  ▶  OpenClaw (Orchestrator, Intent Router, Expense Agent, Recruitment Agent)
                             │  Justina Tool API — internal only
                             ▼
                       justina-app  (C#/.NET execution layer)
                             │
        ┌────────────────────┼────────────────────┐
        ▼                    ▼                    ▼
   Expense API       Recruitment API       OpenAI Vision
    EXTERNAL             EXTERNAL             EXTERNAL

                       SQL Server  — state, idempotency, audit
```

Everything belonging to Justina runs in Docker on one network, addressing peers by service name.

## What works today

- **Expense, end to end in code**: receive an image or PDF, read it with Vision AI, validate every value in
  C#, show it, accept plain-language corrections, require explicit confirmation, submit once.
- JPEG, PNG, WEBP and PDF — text PDFs, scanned PDFs, multi-page. Several receipts in one document become
  several expenses, never one.
- Four independent duplicate-prevention mechanisms. Confirming twice cannot create two expenses.
- Capability-based authorization decided in C#, never by the model.
- 112 automated tests, all passing.

## What does not work yet

- **The Expense API contract has not been supplied.** Submission is built against a documented provisional
  contract and verified against a stub. Real submission is blocked on that specification and credentials.
- **Recruitment is routing-only.** Requests reach the Recruitment Agent, which says honestly that search is
  not connected. No candidate search exists.
- The system has not yet been run against live channels or a live Expense API.

## Quick start

```bash
cp .env.example .env          # fill in MSSQL_SA_PASSWORD, JUSTINA_TOOL_SECRET, NGROK_AUTHTOKEN, OPENAI_API_KEY
dotnet build Justina.slnx
for p in tests/*/; do dotnet test "$p" --nologo; done   # runs offline, no DB or network
docker compose up --build
```

Then grant yourself capabilities and try a tool call — see
[docs/02-developer/getting-started.md](docs/02-developer/getting-started.md).

## Documentation

| Audience | Start here |
|---|---|
| Anyone | [Architecture overview](docs/01-architecture/overview.md) |
| Developers | [Getting started](docs/02-developer/getting-started.md) · [C# architecture](docs/02-developer/csharp-architecture.md) · [Tool API](docs/02-developer/tools.md) |
| QA | [Test strategy](docs/03-qa/test-strategy.md) · [Test cases](docs/03-qa/test-cases.md) |
| Product | [Product overview](docs/04-product/product-overview.md) · [Business rules](docs/04-product/business-rules.md) |

Also: [`plan/task.md`](plan/task.md) — the approved architecture plan · [`task_list.md`](task_list.md) —
delivery status · [`test/test-report.md`](test/test-report.md) — QA results.

## Stack

.NET 10 · SQL Server 2022 · EF Core 10 · PdfPig · PDFium · OpenAI Vision · OpenClaw · NGINX · ngrok ·
Docker Compose

## Repository layout

```text
src/     11 projects: Core, Expense and Recruitment as Domain/Application/Infrastructure, plus the API host
tests/   5 test projects, including architecture rules that fail the build on a layering violation
docker/  NGINX config, OpenClaw agent prompts and tool declarations
docs/    architecture, developer, QA and product documentation
plan/    the approved plan
```
