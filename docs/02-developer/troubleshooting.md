# Troubleshooting

## Start here

```bash
docker compose ps                  # what is up, what is unhealthy
docker compose logs -f justina-app
docker compose logs -f justina-openclaw
curl -s http://localhost:4040/api/tunnels | jq -r '.tunnels[0].public_url'
```

Every log line carries `CorrelationId` and `ConversationId`. Grab one from the failing turn and filter on
it — it follows the request from the edge through to the outbound API call.

## The stack will not start

**`justina-sqlserver` exits immediately.**
Almost always the SA password: it must be 8+ characters with upper, lower, digit and symbol. Check
`docker compose logs justina-sqlserver` for the policy message. On ARM64 the image will not run at all —
run SQL Server outside Docker and point `ConnectionStrings__Justina` at it.

**Compose refuses with "set MSSQL_SA_PASSWORD in .env".**
Working as designed. `.env` is missing or incomplete. `cp .env.example .env` and fill it in.

**`justina-app` restarts in a loop.**
Look for `Database migration failed`. Usually SQL Server is reachable but the credentials or database name
are wrong, or the container came up before SQL Server was healthy — check that `depends_on` still has
`condition: service_healthy`.

## Tool calls fail

| Symptom | Cause |
|---|---|
| `503` on every tool call | No `ToolApi:SharedSecret` configured — it fails closed deliberately |
| `401` | `JUSTINA_TOOL_SECRET` differs between `justina-app` and `justina-openclaw` |
| `404` | You are calling through NGINX; the tool API is internal only |
| `403` with `unauthorized` | The user has no `Principals` row, or lacks the capability |

The `403` case is the most common one in a fresh environment, and it is not a bug — an unmapped user holds
no capabilities. See [getting-started.md](getting-started.md#4-grant-yourself-permission).

## Nothing arrives from the channel

1. Is the ngrok tunnel up, and is the webhook registered against the **current** public URL? On the free
   plan it changes every restart.
2. `docker compose logs justina-nginx` — do requests reach the proxy at all?
3. `docker compose logs justina-openclaw` — does the gateway see the message?
4. `docker compose logs justina-app` — did a tool call arrive?

Whichever log the trail stops at is the layer to investigate. If it stops between OpenClaw and the app,
the cause is tool registration or the shared secret, not business logic.

## Documents

**"I can only read JPEG, PNG, WEBP images and PDF documents"** — the file's magic bytes are not one of
those. The declared MIME type is deliberately ignored.

**"I could not open that PDF"** — PdfPig could not parse it: corrupt, or password-protected.

**"That file is larger than the 20 MB limit"** — raise `DocumentProcessing:MaxBytes`, and raise
`client_max_body_size` in `docker/nginx/nginx.conf` to match, or NGINX rejects it first.

**Rasterization fails with a native load error** — the runtime image is missing `libfontconfig1` or
`libfreetype6`. Both are installed in `src/Justina.Api/Dockerfile`; check it was not stripped.

**Extraction is poor on a text PDF** — it may be classified as scanned. Compare the average characters per
page against `ScannedTextThresholdPerPage`.

## Vision

| Symptom | Cause |
|---|---|
| `vision_failed`, log says "not configured" | No `OPENAI_API_KEY` |
| `vision_failed` with a 4xx in the log | Model name wrong, or the strict schema was rejected |
| "took too long" | Raise `OpenAiVision:TimeoutSeconds`, or lower `MaxRenderedPages` |
| "No receipt could be read" | The model returned zero candidates — usually a genuinely unreadable image |

The provider's error body is in the log, truncated to 500 characters. It is deliberately not shown to the
user.

## Expense submission

**"Expense submission is not available right now"** — `EXPENSE_API_URL` is blank. Expected today: the
contract has not been supplied (plan risk R1).

**Submission failed but the receipt is gone from the conversation** — it is not gone. It is in
`SUBMISSION_FAILED` and retryable through `justina.expense.retry_submission`, without re-confirmation.

**Worried a retry created two expenses** — check `Receipts.ExternalExpenseId`. A filtered unique index
prevents two receipts sharing one, and the submission carries a content-derived idempotency key.

## Data questions

```sql
-- what happened to a receipt, in order
SELECT EventType, FromState, ToState, Actor, CreatedAtUtc
FROM ReceiptEvents WHERE ReceiptId = '...' ORDER BY Id;

-- what a conversation is currently working on
SELECT * FROM Conversations WHERE ExternalConversationId = '...';

-- receipts found in one document
SELECT Id, SequenceInBatch, State, Merchant, Amount FROM Receipts
WHERE BatchId = '...' ORDER BY SequenceInBatch;

-- who can do what
SELECT Channel, UserId, DisplayName, CapabilitiesJson FROM Principals;
```

`ReceiptEvents` is append-only and written by the aggregate itself, so it is the authoritative answer to
"what actually happened".

## Reset

```bash
docker compose down -v     # deletes the database and every receipt in it
docker compose up --build
```

## Build and test

**Warnings are errors** — `Directory.Build.props`. Usually a real nullability or unused-symbol problem.

**`grep` says a source file is binary** — it contains a stray control character. Find it with
`tr -cd '\000' < file | wc -c` and strip it with `tr -d '\000'`.

**`dotnet ef` cannot create the context** — `DesignTimeDbContextFactory` must list every domain's
`IModelConfiguration`. A new domain missing from it silently disappears from migrations.
