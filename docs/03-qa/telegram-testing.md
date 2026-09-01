# Telegram Testing

Telegram is the first channel to bring up. It needs no app review, a bot token takes two minutes to
create, and it exercises exactly the same document and receipt code as WhatsApp.

This document covers the Telegram-specific parts: how a message reaches Justina, how media is
downloaded, how replies go out, and what happens when each of those fails. The receipt journey itself
(extraction, editing, confirmation) is in [receipt-testing.md](receipt-testing.md), and document
handling is in [pdf-testing.md](pdf-testing.md). Test those once; here you only prove that Telegram
feeds them correctly.

## Before you start

**Blocker.** `justina-app` currently exits at startup with
`System.NotSupportedException: Globalization Invariant Mode is not supported.` No Telegram journey can
be completed until that is fixed. See [test-environment.md](test-environment.md). The tool-API cases
in this document that do not touch the database still run.

You need:

| Thing | How to get it |
|---|---|
| A Telegram bot token | Message `@BotFather` on Telegram, `/newbot`, copy the token into `TELEGRAM_BOT_TOKEN` in `.env` |
| A test Telegram account | Any account. Start a chat with your bot and send `/start` |
| Your numeric Telegram user id | Message `@userinfobot`, or read it from the app logs on the first inbound message |
| A row in `Principals` | Nothing works without it. See [test-environment.md](test-environment.md) |
| The ngrok public URL | `curl -s http://127.0.0.1:4040/api/tunnels` |

The `Principals` row must use `Channel = 1` for Telegram and your numeric Telegram user id as
`UserId`. There is no seeding code, so an unmapped user has no capabilities and every protected
action returns `unauthorized`.

## How a Telegram message actually reaches Justina

```
You, in Telegram
      |
Telegram servers
      |
ngrok tunnel  ->  justina-nginx  ->  justina-openclaw
                                          |
                                     HTTP tool call
                                          |
                                      justina-app
```

OpenClaw owns the transport. It runs the Telegram channel plugin, receives the update, and calls
Justina's HTTP tools. **`justina-app` has no Telegram webhook endpoint.** Its only routes are
`/tools/*`, `/health/live` and `/health/ready`. Do not go looking for a webhook handler in the C#
code; there isn't one, and that is deliberate.

C# still owns everything that matters: downloading the file from Telegram, validating it,
deduplicating retries, holding the receipt state, and deciding what is allowed.

## Setting the webhook

The ngrok URL rotates every restart on the free plan. Re-register it each time:

```bash
NGROK_URL=$(curl -s http://127.0.0.1:4040/api/tunnels | grep -o 'https://[^"]*ngrok[^"]*' | head -1)
echo "$NGROK_URL"

curl -s "https://api.telegram.org/bot$TELEGRAM_BOT_TOKEN/setWebhook?url=$NGROK_URL/telegram"
curl -s "https://api.telegram.org/bot$TELEGRAM_BOT_TOKEN/getWebhookInfo"
```

`getWebhookInfo` is the fastest way to diagnose a silent bot. If `last_error_message` is populated,
Telegram is reaching something and getting an error back. If `pending_update_count` keeps climbing,
nothing is consuming the updates.

The exact webhook path depends on how the pinned OpenClaw image mounts its Telegram plugin. That is
unverified in this repository (plan risk R3). Confirm it from the OpenClaw container's own logs
before you assume the path above.

## Journeys

Run these in order. Each one assumes the previous state was cleaned up (cancel any receipt still in
progress).

### T1 — Plain text, no workflow active

**Steps.** Send `hello` to the bot.

**Expected.** A conversational reply from the Orchestrator. No receipt is created.

**Verify.**

```sql
SELECT ActiveWorkflow FROM Conversations
WHERE Channel = 1 AND ExternalConversationId = '<your chat id>';
```

`ActiveWorkflow` must be `NULL`. If a plain greeting started an expense workflow, that is a routing
failure — see [agent-routing-testing.md](agent-routing-testing.md).

### T2 — Image receipt

**Steps.** Send a photo of a receipt as a Telegram **photo** (not as a file).

**Expected.** The bot replies with the extracted fields and asks whether they are correct. Nothing is
submitted.

**Verify.**

```sql
SELECT Id, State, Merchant, Amount, Currency, ExternalExpenseId
FROM Receipts ORDER BY CreatedAtUtc DESC;
```

`State` must be `3` (`WaitingConfirmation`) and `ExternalExpenseId` must be `NULL`. A non-null
external id at this point means the receipt was submitted before you confirmed it, which is a
business-rule failure.

Also check the audit trail:

```sql
SELECT EventType, FromState, ToState, Actor, CreatedAtUtc
FROM ReceiptEvents WHERE ReceiptId = '<id>' ORDER BY Id;
```

You should see `Created`, `ExtractionStarted`, `ExtractionCompleted`.

### T3 — PDF receipt

**Steps.** Send a receipt PDF as a Telegram **document**.

**Expected.** Same as T2. The reply must contain the fields read from the PDF, not from a filename.

**Verify.** The app log line `Receipt {ReceiptId} received from {DocumentKind} with {PageCount} page(s)`
tells you which path was taken — `TextPdf` or `ScannedPdf` — and how many pages were read. A
multi-page PDF that reports `PageCount` 1 means only the first page reached the processor.

Everything else about PDFs belongs in [pdf-testing.md](pdf-testing.md). Here you are only confirming
that a Telegram document arrives intact.

### T4 — Edit

**Steps.** With a receipt awaiting confirmation, send `amount should be 15.50`.

**Expected.** The bot re-displays the **complete** receipt with the new amount and asks for
confirmation again. Only the amount changed.

**Verify.**

```sql
SELECT EventType, PayloadJson FROM ReceiptEvents
WHERE ReceiptId = '<id>' AND EventType = 'Edited';
```

The payload lists the fields that changed. It must contain `Amount` and nothing else. The receipt
stays in state `3`.

Try a rejected edit too: `currency should be Dollars`. Expect a refusal relayed in plain language,
built from `validation_failed` — "Currency needs a three-letter ISO-4217 currency code, for example
SGD." The receipt is unchanged.

### T5 — Confirm

**Steps.** Reply `yes`.

**Expected.** The bot confirms the submission and quotes the expense reference it got back.

**Verify.** State becomes `6` (`Submitted`) and `ExternalExpenseId` is populated. `ReceiptEvents`
shows `Confirmed`, `SubmissionStarted`, `Submitted`.

With no `EXPENSE_API_URL` configured — the current situation — expect a refusal instead:
`not_available`, "Expense submission is not available right now." The receipt stops at
`SubmissionFailed` (state `7`) and stays retryable. That is correct behaviour, not a bug. Point a
WireMock stub at `EXPENSE_API_URL` to exercise the success path; see
[api-testing.md](api-testing.md).

### T6 — Cancel

**Steps.** Send a receipt, then send `cancel` before confirming.

**Expected.** The bot confirms nothing was submitted.

**Verify.** State becomes `8` (`Cancelled`), `ExternalExpenseId` stays `NULL`, and
`Conversations.ActiveWorkflow` is back to `NULL`. If you have a WireMock stub in front of the Expense
API, its request log must be empty for this receipt.

### T7 — Duplicate delivery

Telegram retries an update when the webhook does not answer in time. Justina must not create a second
receipt.

**Steps.** Send a photo. Wait for the reply. Then, from the host, replay the same tool call twice with
the same `messageId`:

```bash
TOOL_SECRET=$(grep JUSTINA_TOOL_SECRET .env | cut -d= -f2)

curl -s -X POST http://localhost:8080/tools/expense.receive_media \
  -H "Content-Type: application/json" \
  -H "X-Justina-Tool-Key: $TOOL_SECRET" \
  -d '{
        "envelope": {"channel":"telegram","userId":"<your id>","conversationId":"<chat id>","messageId":"dup-1"},
        "media": {"mediaId":"<file id>","mimeType":"image/jpeg","fileName":"receipt.jpg","sizeBytes":12345}
      }'
```

Run it a second time, unchanged.

**Expected.** Both calls return `ok: true`. The second returns the receipt that already exists rather
than creating another one.

**Verify.**

```sql
SELECT COUNT(*) FROM InboundMessages WHERE Channel = 1 AND MessageId = 'dup-1';   -- 1
SELECT COUNT(*) FROM Receipts WHERE SourceMediaId = '<file id>';                  -- 1
```

`InboundMessages` has a composite primary key on `(Channel, MessageId)`, and
`justina.expense.receive_media` checks it before it does anything else. If `messageId` is absent from
the envelope, the media id is used instead — so a replay with no message id still deduplicates,
provided the file is the same.

## Failure cases

These are the Telegram adapter's own failure paths, all in
`src/Justina.Core.Infrastructure/Channels/Telegram/TelegramAdapter.cs`.

| Case | How to trigger | Expected error code | Expected message |
|---|---|---|---|
| No bot token | Blank `TELEGRAM_BOT_TOKEN`, restart, call `receive_media` | `not_available` | "Telegram is not available right now." |
| Unknown or expired file id | Call `receive_media` with `"mediaId":"nonexistent"` | `not_found` | "That Telegram file is no longer available." |
| Download fails | Wrong `Telegram:ApiBaseUrl`, or block outbound network | `document_unreadable` | "I could not download that file from Telegram." |
| Reply fails | Revoke the bot token while a workflow is open | `external_api_failed` | "I could not send that message to Telegram." |
| Oversized file | Send a file over 20 MB | `media_too_large` | "That file is larger than the 20 MB limit." |
| Missing media reference | Call `receive_media` with an empty `mediaId` | `validation_failed` | "No media reference was supplied." |

Two notes on the size limit. NGINX independently caps request bodies at `client_max_body_size 25m`,
so a very large upload is refused at the edge with a 413 before it ever reaches C#. And Telegram's own
Bot API caps downloads at 20 MB regardless of what Justina allows, so the "oversized" path is more
easily exercised through the tool API with a large local file than through the Telegram client.

The `not_found` case is worth running deliberately. Telegram file ids expire, and a receipt that sat
in a queue overnight will hit exactly this path.

## Exercising the same code without Telegram

You do not need a bot token to test most of this. The tool API is the same entry point OpenClaw uses.
From inside the Docker network, or with the app port published locally:

```bash
TOOL_SECRET=$(grep JUSTINA_TOOL_SECRET .env | cut -d= -f2)

# What does the backend think is going on in this conversation?
curl -s -X POST http://localhost:8080/tools/session.context \
  -H "Content-Type: application/json" \
  -H "X-Justina-Tool-Key: $TOOL_SECRET" \
  -d '{"envelope":{"channel":"telegram","userId":"123456","conversationId":"123456"}}'
```

Every tool takes the same envelope: `channel`, `userId`, `conversationId`, and optionally `messageId`
and `correlationId`. `channel` must be `telegram` or `whatsapp`.

Useful negative checks that need no credentials at all:

```bash
# No key at all -> 401
curl -s -o /dev/null -w "%{http_code}\n" -X POST http://localhost:8080/tools/session.context \
  -H "Content-Type: application/json" -d '{"envelope":{"channel":"telegram","userId":"1","conversationId":"1"}}'

# Wrong channel -> 200 with ok:false
curl -s -X POST http://localhost:8080/tools/session.context \
  -H "Content-Type: application/json" -H "X-Justina-Tool-Key: $TOOL_SECRET" \
  -d '{"envelope":{"channel":"discord","userId":"1","conversationId":"1"}}'
```

The second returns exactly:

```json
{"ok":false,"error":{"code":"validation_failed","message":"'discord' is not a supported channel."}}
```

An empty `userId` returns "The request envelope needs a user id and a conversation id."

Remember the response convention: a refusal is a **successful HTTP call** with `"ok": false`, so the
agent can relay the reason. Only `unauthorized` gets its own status code, HTTP 403. `GET` on a tool
path returns 405 — they are all POST.

## Reading the logs

```bash
docker compose logs -f justina-app
docker compose logs -f justina-openclaw
```

Logs are structured JSON. Every command carries `CorrelationId`, `ConversationId`, `Channel` and
`CommandType`. Follow one message end to end by grepping for its correlation id.

The lines worth knowing:

- `Receipt {ReceiptId} received from {DocumentKind} with {PageCount} page(s)` — intake succeeded.
- `Extracted {ReceiptCount} receipt(s) from media of receipt {ReceiptId} using {Model}` — Vision
  answered. A count above 1 means a batch.
- `Command {CommandType} refused with {ErrorCode} in {DurationMs}ms` — a deliberate refusal.
- `Telegram getFile failed with {StatusCode}` — a download problem, not a Justina problem.

The bot token appears in the Telegram request URL path. Check that it does not appear in any log line
or trace attribute; see [security-testing.md](security-testing.md).

## What cannot be tested here

- **A live Telegram round trip** needs a real bot token and a working tunnel. Nothing in the automated
  suite covers it.
- **The OpenClaw side** — whether the pinned image registers Justina's tools the way
  `docker/openclaw/openclaw.json.template` assumes — is unverified (plan risk R3). If the bot goes
  quiet with no error in `justina-app`, suspect this first and check the OpenClaw container logs.
- **Telegram's `update_id` deduplication** is OpenClaw's responsibility. Justina deduplicates on
  `messageId`, which is what T7 tests. If OpenClaw passes a fresh `messageId` for a retried update,
  Justina will treat it as new. Verify what OpenClaw actually sends before signing this off.
