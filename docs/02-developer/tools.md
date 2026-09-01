# The Tool API

The only surface the AI layer can act through. Eight `POST` endpoints under `/tools`, defined in
`src/Justina.Api/Tools/ToolEndpoints.cs` and declared to agents in
`docker/openclaw/tools/justina-tools.json`.

## Authentication

Every call carries a shared secret:

```
X-Justina-Tool-Key: <JUSTINA_TOOL_SECRET>
```

`ToolApiKeyMiddleware` compares it in fixed time. A missing configured secret **fails closed** with `503`
rather than allowing calls through. A wrong key gets `401`. NGINX returns `404` for `/tools/`, so the
surface is not reachable from the internet at all.

## The envelope

```json
{
  "channel": "telegram",
  "userId": "12345",
  "conversationId": "12345",
  "messageId": "678",
  "correlationId": "optional"
}
```

The agent supplies identity **claims**. `RequestContextFactory` resolves them to a principal through
`IAuthorizationService`; capabilities come from the database, never from the request. An agent cannot
inflate its own permissions by asserting them.

An unknown channel or a missing user/conversation id is a `validation_failed` refusal.

## Response shape

```json
{ "ok": true,  "data": { ... } }
{ "ok": false, "error": { "code": "invalid_workflow_state", "message": "..." } }
```

Refusals return **HTTP 200** with `ok: false`, so the agent relays a reason instead of treating a business
decision as a transport failure. `unauthorized` is the exception: it returns `403` so it appears in access
logs and metrics.

## Endpoints

### `POST /tools/session.context`

Returns capabilities, display name, and the active workflow plus its entity id. Agents call this first
every turn. This is what lets the Intent Router keep a conversation coherent without holding state.

### `POST /tools/expense.receive_media`

```json
{ "envelope": {...}, "media": { "mediaId": "...", "mimeType": "application/pdf",
                                "fileName": "receipt.pdf", "sizeBytes": 12345 } }
```

Deduplicates the message id, then runs `ReceiveReceiptCommand` followed by `ExtractReceiptCommand`, so one
agent call produces something a person can review.

Returns a `ReceiptExtractionOutcome`: `receiptCount`, `batchId`, and a snapshot per receipt. A
`receiptCount` above 1 means the agent must ask before anything proceeds.

A repeated `messageId` does not reprocess: the current outcome is returned instead.

Requires `expense.submit`.

### `POST /tools/expense.get_receipt`

Omit `receiptId` to get the conversation's active receipt. Returns the snapshot the agent renders,
including `awaitingConfirmation`, `isSubmittable` and `missingField`.

Requires `expense.read`.

### `POST /tools/expense.edit_receipt`

```json
{ "envelope": {...}, "edits": [ { "field": "amount", "value": "15.50" } ] }
```

Accepted field names, including synonyms: `merchant`/`vendor`/`store`, `date`, `currency`,
`amount`/`total`, `category`, `receiptNumber`/`invoice`, `tax`/`gst`/`vat`.

Values are strings and are parsed by C#. `"August 30, 2026"`, `"idr"` and `"1,234.56"` all work; an
unparseable value is refused with a message saying what is acceptable. The same field twice is refused
rather than silently taking the last one.

Only the named fields change. The receipt stays in `WAITING_CONFIRMATION`, so the agent must show it again
and ask again.

Requires `expense.submit`.

### `POST /tools/expense.confirm_receipt`

The only path to the Expense API. Legal only from `WAITING_CONFIRMATION`; a receipt already `SUBMITTED`
returns its existing snapshot rather than submitting twice. An incomplete receipt is refused with the
missing field named.

Idempotent on `confirm:{receiptId}`.

Requires `expense.submit`.

### `POST /tools/expense.cancel_receipt`

Moves to `CANCELLED` and clears the active workflow. Nothing is submitted. Cancelling an already-cancelled
receipt succeeds; cancelling a submitted one is refused.

Requires `expense.submit`.

### `POST /tools/expense.retry_submission`

Retries a submission that failed after confirmation. Legal from `CONFIRMED`, `SUBMISSION_FAILED` or
`SUBMITTED`; anything else is refused with `invalid_workflow_state`. The user is not asked to confirm
again — they already did — and the idempotency key is derived from the same content, so a retry the
Expense API already processed resolves to the same expense.

Requires `expense.submit`.

### `POST /tools/recruitment.search_candidates`

Accepts `role`, `skills`, `seniority`, `location`. Currently returns `not_available`, because the
Recruitment API is not connected. A request with no criteria at all is refused with `validation_failed`
rather than searching for everything.

Requires `recruitment.search`.

## Error codes

From `src/Justina.Core.Domain/Results/Error.cs`:

| Code | Meaning |
|---|---|
| `validation_failed` | The request or a value is not acceptable |
| `not_found` | No such receipt, or nothing in progress |
| `invalid_workflow_state` | Legal action, wrong moment |
| `unauthorized` | The principal lacks the capability |
| `conflict` | Concurrent change, or already recorded |
| `unsupported_media` | Not JPEG, PNG, WEBP or PDF |
| `media_too_large` | Over the configured size cap |
| `document_unreadable` | Corrupt, empty, or could not be downloaded |
| `too_many_pages` | Over the configured page cap |
| `vision_failed` | The document could not be read |
| `external_api_failed` | The external system failed; retryable |
| `not_available` | The capability is not configured |

These are a contract. The agent relays them, so they never carry secrets or internal detail.

## Adding a tool

1. Add the command or query and register its handler.
2. Add an endpoint in `ToolEndpoints`, translating the contract and nothing more — no business logic.
3. Declare it in `justina-tools.json` with a description written for the model.
4. Tell the relevant agent prompt when to use it.
