# System Architecture

## Request lifecycle

A person sends a photo of a receipt on Telegram. This is what happens.

```text
 1. Telegram delivers the message to the OpenClaw gateway (through ngrok → NGINX).
 2. The Orchestrator agent calls justina.session.context.
      → C# resolves the channel identity to a principal and reports the active workflow.
 3. The Intent Router picks a domain. An active workflow wins; otherwise it classifies the message.
 4. The Expense Agent calls justina.expense.receive_media with the channel's media reference.
 5. C# deduplicates the message id, downloads the media, sniffs its real type, checks size and pages,
    stores it, and creates a Receipt in state RECEIVED.
 6. C# runs extraction: the document goes to the Vision provider under a strict JSON schema.
 7. C# normalizes and validates every returned value, then moves the receipt to WAITING_CONFIRMATION.
 8. The agent renders the receipt and asks the user whether it is correct.
 9. Corrections → justina.expense.edit_receipt → validated → still WAITING_CONFIRMATION → shown again.
10. Explicit "yes" → justina.expense.confirm_receipt → CONFIRMED → SUBMITTING → Expense API → SUBMITTED.
```

Nothing between steps 1 and 9 can reach the Expense API. Step 10 is the only path, and it is idempotent.

## The normalized message envelope

Every channel produces the same shape. Business logic never sees a Telegram or WhatsApp structure.

```json
{
  "channel": "whatsapp",
  "userId": "...",
  "conversationId": "...",
  "messageId": "...",
  "type": "document",
  "text": null,
  "media": {
    "mediaId": "...",
    "mimeType": "application/pdf",
    "fileName": "receipt.pdf",
    "sizeBytes": 12345
  },
  "receivedAtUtc": "2026-09-01T10:00:00Z"
}
```

Defined by `InboundMessage` in `src/Justina.Core.Domain/Messaging/InboundMessage.cs`. Adding a channel
means writing two small adapters, not touching a domain rule.

## The tool surface

Seven endpoints, all `POST`, all under `/tools`, all carrying the same envelope.

| Tool | Kind | What it does |
|---|---|---|
| `justina.session.context` | query | Who the user is, what they may do, which workflow is active |
| `justina.expense.receive_media` | command | Register and read inbound media; returns extracted receipts |
| `justina.expense.get_receipt` | query | Current receipt snapshot for display |
| `justina.expense.edit_receipt` | command | Apply named field changes |
| `justina.expense.confirm_receipt` | command | Validate, confirm, submit — idempotent |
| `justina.expense.cancel_receipt` | command | Abandon; submits nothing |
| `justina.recruitment.search_candidates` | query | Candidate search (not connected yet) |

Refusals come back as HTTP 200 with `ok: false` and a code plus a human-readable message, so the agent
relays a reason rather than treating a business decision as a transport error. Unauthorized is the one
exception: it returns 403 so it shows up in access logs and metrics.

## The receipt state machine

C# owns this. Illegal transitions throw; they never silently succeed.

```text
RECEIVED ──▶ EXTRACTING ──▶ WAITING_CONFIRMATION ──▶ CONFIRMED ──▶ SUBMITTING ──▶ SUBMITTED
                 │                   │  ▲                                │
                 │                   │  └── edit (validated) ────────────┘
                 ▼                   ▼                                   ▼
         EXTRACTION_FAILED       CANCELLED                       SUBMISSION_FAILED
                                                                         │
                                                                         └─▶ retry: SUBMITTING
```

Implemented in `src/Justina.Expense.Domain/Receipt.cs`. Every transition writes a `ReceiptEvent`, so the
audit trail cannot drift from the state.

## Concurrency and duplicates

Four independent mechanisms, because retries arrive from four different directions:

1. **Inbound deduplication** — a unique key on `(Channel, MessageId)`. A replayed webhook is dropped.
2. **Command idempotency** — the first result for a command key is stored and replayed. Failures are
   never stored, so a transient error stays retryable.
3. **Submission key** — a SHA-256 of the receipt id plus its confirmed content, sent to the Expense API
   as an idempotency header and enforced locally by a filtered unique index on `ExternalExpenseId`.
4. **Optimistic concurrency** — a SQL Server `rowversion` on `Receipts`. Two simultaneous confirmations
   cannot both win; the loser gets a typed conflict.

## Persistence

SQL Server 2022 via EF Core 10. Tables: `Conversations`, `Receipts`, `ReceiptLineItems`, `ReceiptEvents`,
`ReceiptBatches`, `InboundMessages`, `IdempotencyKeys`, `Principals`.

Deliberate type choices: money is `decimal(18,2)`, quantities `decimal(18,4)`, timestamps `datetime2`
stored UTC, concurrency `rowversion`, JSON payloads `nvarchar(max)`. The unique index on
`ExternalExpenseId` is filtered, because SQL Server treats multiple NULLs as duplicates and an unfiltered
index would block every receipt that has not been submitted yet.

## Observability

Every log line and span carries `CorrelationId`, `ConversationId`, `Channel` and `CommandType`; command
logs add duration and outcome. Credentials, tokens and authorization headers are never logged. Provider
error bodies are logged truncated for diagnosis and never relayed to the user.
