# Justina — Architecture Overview

Justina is an AI business assistant that people talk to over **WhatsApp** and **Telegram**. It turns
natural-language requests into deterministic business operations across two domains: **Expense** and
**Recruitment**.

## The one idea that shapes everything

**The AI proposes; C# disposes.**

The language model is excellent at understanding what someone meant — "the amount is wrong, it's 15.50",
"find me senior .NET people", a photo of a crumpled receipt. It is not a safe place to keep money,
permissions, or workflow state. So Justina splits along exactly that line:

| Concern | Owner |
|---|---|
| Understanding language, conversation, tone | **AI layer (OpenClaw)** |
| Choosing which specialist handles a message | **AI layer** |
| Deciding whether an action is allowed | **C#** |
| Deciding whether a value is valid | **C#** |
| Knowing what state a receipt is in | **C#** |
| Calling an external business API | **C#** |
| Preventing a duplicate expense | **C#** |

Everything the AI can do, it does by calling a **named tool** with typed arguments. There is no generic
HTTP tool, no shell, no database access. A tool call that would be illegal in the current state comes
back as a typed refusal the agent must relay — it cannot be argued past.

## The layers

```text
                              INTERNET
                    ┌────────────┴────────────┐
                 WhatsApp                  Telegram
                    └────────────┬────────────┘
                                 ▼
                        justina-ngrok        public ingress
                                 ▼
                        justina-nginx        reverse proxy, limits, headers
                                 ▼
                      justina-openclaw       AI / agent layer
                      ├── Orchestrator
                      ├── Intent Router
                      ├── Expense Agent
                      └── Recruitment Agent
                                 │ Justina Tool API (HTTP, shared secret, internal only)
                                 ▼
                        justina-app          C#/.NET execution layer
                      ├── Channel adapters + normalization
                      ├── Media / PDF processing
                      ├── Vision capability
                      ├── Expense domain (state machine, CQRS)
                      ├── Recruitment domain
                      └── API clients
                                 │
                 ┌───────────────┼────────────────┐
                 ▼               ▼                ▼
          Expense API    Recruitment API    OpenAI Vision API
           EXTERNAL         EXTERNAL            EXTERNAL

                        justina-sqlserver    state, idempotency, audit
```

## What lives where

**OpenClaw** hosts four agents. It owns transport (the WhatsApp and Telegram channel plugins), sessions,
and conversation. It owns no business truth: no credentials, no workflow state, no authorization.

**justina-app** is the C#/.NET execution layer. It owns the normalized message contract, media download
and validation, document processing, Vision, the receipt state machine, validation, authorization,
idempotency, persistence, and every call to an external business API.

**SQL Server** holds the authoritative state: conversations, receipts, line items, the audit trail,
inbound-message deduplication, idempotency keys, and principals.

**External and not ours:** the Expense API, the Recruitment API, and the OpenAI Vision API. None of them
run in the Justina Docker environment.

## The vertical slice that is built

Receipt → expense, end to end:

```text
User sends an image or PDF
        ↓  justina.expense.receive_media
Media downloaded, sniffed, size- and page-checked, stored
        ↓
Vision reads it against a strict JSON schema
        ↓
C# normalizes and validates every value
        ↓
Receipt shown to the user           ← nothing has been submitted
        ↓  justina.expense.edit_receipt   (repeatable)
Only the named fields change; the full receipt is shown again
        ↓  justina.expense.confirm_receipt
Explicit confirmation
        ↓
Submitted exactly once to the Expense API
```

## Domain isolation

Expense and Recruitment are separate domains with their own Domain, Application and Infrastructure
projects. Neither references the other, and an architecture test fails the build if that ever changes.
A recruitment request cannot reach the Expense API because there is no code path from one to the other —
not because a prompt asks it not to.

## Where to read next

| You want to know | Read |
|---|---|
| How the pieces fit at runtime | [system-architecture.md](system-architecture.md) |
| What each container is for | [docker-architecture.md](docker-architecture.md) |
| How the agents think and route | [ai-architecture.md](ai-architecture.md) |
| How documents get read | [vision-architecture.md](vision-architecture.md) |
| How external systems are reached | [integration-architecture.md](integration-architecture.md) |
| How to run it | [../02-developer/getting-started.md](../02-developer/getting-started.md) |
