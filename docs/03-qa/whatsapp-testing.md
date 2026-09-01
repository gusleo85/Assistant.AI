# WhatsApp Testing

WhatsApp runs on the same abstractions as Telegram. The document pipeline, the receipt state machine,
the validation rules and the tool contract are identical — only the adapter that fetches media and
sends replies differs.

So do not re-test the receipt journey here. Test it once on Telegram
([telegram-testing.md](telegram-testing.md)), test documents once in
[pdf-testing.md](pdf-testing.md), and use this document to prove three WhatsApp-specific things:
media download works, the envelope is right, and replies go out. Plus one thing that matters more on
WhatsApp than anywhere else — webhook signature verification.

## Before you start

**Blocker.** `justina-app` currently exits at startup with
`System.NotSupportedException: Globalization Invariant Mode is not supported.` No WhatsApp journey can
be completed until that is fixed. See [test-environment.md](test-environment.md).

**Second blocker, practical.** WhatsApp Business onboarding is slow and outside your control: a Meta
app, a business account, a verified phone number, and for some message types a template review. This
is plan risk R6. Bring Telegram up first. Do not let WhatsApp block the rest of the test pass.

You need:

| Thing | `.env` variable | Notes |
|---|---|---|
| A permanent access token | `WHATSAPP_ACCESS_TOKEN` | Temporary tokens expire in 24 hours and will waste a morning |
| The phone number id | `WHATSAPP_PHONE_NUMBER_ID` | From the Meta app dashboard. Not the phone number itself |
| The app secret | `WHATSAPP_APP_SECRET` | Used to verify `X-Hub-Signature-256` |
| A webhook verify token | `WHATSAPP_WEBHOOK_VERIFY_TOKEN` | Any string you choose; Meta echoes it back during subscription |
| A test phone number | — | Added to the app's allowed testers |
| A row in `Principals` | — | `Channel = 2` for WhatsApp, `UserId` is the sender's phone number in international format without `+` |

There is no seeding code for `Principals`. Without a row, every protected action returns
`unauthorized`. The SQL is in [test-environment.md](test-environment.md).

## How a WhatsApp message reaches Justina

```
Your phone
     |
WhatsApp Cloud API
     |
ngrok tunnel  ->  justina-nginx  ->  justina-openclaw
                                          |
                                     HTTP tool call
                                          |
                                      justina-app
```

Same shape as Telegram. OpenClaw runs the WhatsApp channel plugin and terminates the webhook.
**`justina-app` exposes no WhatsApp webhook endpoint** — only `/tools/*`, `/health/live` and
`/health/ready`. Everything after the webhook is shared C# code.

Register the callback URL in the Meta app dashboard:

```bash
curl -s http://127.0.0.1:4040/api/tunnels
```

Use that HTTPS URL plus the path OpenClaw's WhatsApp plugin listens on, and the verify token from
`.env`. Meta will immediately GET the URL with `hub.mode=subscribe`, `hub.challenge` and
`hub.verify_token`, and expects the challenge echoed back. If subscription fails, the tunnel or the
verify token is wrong — check `justina-nginx` and `justina-openclaw` logs, in that order.

The URL rotates on every ngrok restart on the free plan (plan risk R7), and re-registering a webhook
in the Meta dashboard is slower than Telegram's one-line `setWebhook`. Budget for it.

## Signature verification — read this before signing anything off

`WhatsAppOptions` declares two properties:

```csharp
/// <summary>Used to verify the X-Hub-Signature-256 header on inbound webhooks.</summary>
public string AppSecret { get; set; } = string.Empty;

/// <summary>Echoed back during webhook subscription setup.</summary>
public string WebhookVerifyToken { get; set; } = string.Empty;
```

**Nothing in this repository reads either of them.** Searching the C# source for `AppSecret` finds
only the property declaration and its comment. There is no HMAC check, no signature comparison, no
webhook verification code anywhere in `src/`.

That is not automatically a defect. Under the hybrid channel design, OpenClaw owns the transport, and
both values are passed to `justina-openclaw` in `docker-compose.yml` and named in
`docker/openclaw/openclaw.json`. Verification is *expected* to happen in the gateway's
WhatsApp plugin.

But whether the pinned OpenClaw image actually enforces it is **unverified** (plan risk R3). Nobody
has tested it. So test it.

### W-SEC-01 — Unsigned webhook must be rejected

**Steps.** Capture a genuine inbound webhook body from the OpenClaw logs. Replay it at the public
webhook URL with no signature header at all:

```bash
NGROK_URL=$(curl -s http://127.0.0.1:4040/api/tunnels | grep -o 'https://[^"]*ngrok[^"]*' | head -1)

curl -s -o /dev/null -w "%{http_code}\n" -X POST "$NGROK_URL/<whatsapp webhook path>" \
  -H "Content-Type: application/json" \
  --data @captured-webhook.json
```

**Expected.** Rejected — 401 or 403 — and no receipt created.

### W-SEC-02 — Wrongly signed webhook must be rejected

Same body, with a signature that does not match:

```bash
curl -s -o /dev/null -w "%{http_code}\n" -X POST "$NGROK_URL/<whatsapp webhook path>" \
  -H "Content-Type: application/json" \
  -H "X-Hub-Signature-256: sha256=0000000000000000000000000000000000000000000000000000000000000000" \
  --data @captured-webhook.json
```

**Expected.** Rejected, and no receipt created.

**If either request is accepted and produces a receipt, stop and raise it as a security finding.** It
means anyone who learns the ngrok URL can inject messages that Justina will treat as coming from a
real WhatsApp user. Record the actual observed status code and whether a row appeared in `Receipts`.
Do not record a pass you did not see.

## Journeys

These mirror the Telegram journeys. Run them only after Telegram passes — a failure here that also
fails on Telegram is not a WhatsApp problem.

| Id | Send | Expected |
|---|---|---|
| W1 | `hello` | Conversational reply. `Conversations.ActiveWorkflow` stays `NULL` |
| W2 | A receipt photo | Extracted fields displayed, nothing submitted. `Receipts.State` = 3, `ExternalExpenseId` `NULL` |
| W3 | A receipt PDF as a document | Same as W2. Log line names the document kind and page count |
| W4 | `amount should be 15.50` | Full receipt re-displayed, only the amount changed, confirmation asked again |
| W5 | `yes` | Submitted, expense reference quoted. State 6 and `ExternalExpenseId` populated |
| W6 | Send a receipt, then `cancel` | Nothing submitted. State 8, `ActiveWorkflow` back to `NULL` |

Verification queries are the same as in [telegram-testing.md](telegram-testing.md), with
`Channel = 2`:

```sql
SELECT Id, State, Merchant, Amount, Currency, ExternalExpenseId
FROM Receipts ORDER BY CreatedAtUtc DESC;

SELECT ActiveWorkflow FROM Conversations
WHERE Channel = 2 AND ExternalConversationId = '<sender phone number>';
```

`Conversations` has a unique index on `(Channel, ExternalConversationId)`. That means the same
identifier on Telegram and on WhatsApp is two separate conversations with separate workflows. Worth
proving once: send a receipt on Telegram, then check that a WhatsApp conversation with a numerically
similar id reports `activeWorkflow: null` from `justina.session.context`.

With no `EXPENSE_API_URL` configured, W5 ends in `not_available`, "Expense submission is not available
right now.", and the receipt sits at `SubmissionFailed` (state 7), retryable. That is correct. See
[api-testing.md](api-testing.md) for running it against a stub.

### W7 — Duplicate delivery

The Cloud API redelivers when your webhook does not answer quickly. Justina must not create a second
receipt.

Replay the same `messageId` twice through the tool API:

```bash
TOOL_SECRET=$(grep JUSTINA_TOOL_SECRET .env | cut -d= -f2)

curl -s -X POST http://localhost:8080/tools/expense.receive_media \
  -H "Content-Type: application/json" \
  -H "X-Justina-Tool-Key: $TOOL_SECRET" \
  -d '{
        "envelope": {"channel":"whatsapp","userId":"6591234567","conversationId":"6591234567","messageId":"wamid.TEST1"},
        "media": {"mediaId":"<media id>","mimeType":"application/pdf","fileName":"receipt.pdf","sizeBytes":54321}
      }'
```

**Expected.** Both calls return `ok: true`; the second returns the existing receipt.

```sql
SELECT COUNT(*) FROM InboundMessages WHERE Channel = 2 AND MessageId = 'wamid.TEST1';  -- 1
```

Deduplication runs before anything else in `justina.expense.receive_media`, backed by the composite
primary key on `(Channel, MessageId)`. When the envelope has no `messageId`, the media id is used
instead.

## Failure cases

All from `src/Justina.Core.Infrastructure/Channels/WhatsApp/WhatsAppAdapter.cs`.

| Case | How to trigger | Error code | Message |
|---|---|---|---|
| No access token | Blank `WHATSAPP_ACCESS_TOKEN`, restart | `not_available` | "WhatsApp is not available right now." |
| Expired media id | Call `receive_media` with an old media id | `not_found` | "That WhatsApp file is no longer available." |
| Lookup returns no URL | Point `WhatsApp:GraphBaseUrl` at a stub returning `{}` | `not_found` | "That WhatsApp file is no longer available." |
| Download fails | Stub returns a URL that 404s | `document_unreadable` | "I could not download that file from WhatsApp." |
| Reply fails | Revoke the token mid-workflow | `external_api_failed` | "I could not send that message to WhatsApp." |
| Oversized file | File over 20 MB | `media_too_large` | "That file is larger than the 20 MB limit." |
| No media reference | Empty `mediaId` | `validation_failed` | "No media reference was supplied." |

The expired-media case matters more on WhatsApp than on Telegram: Cloud API media URLs are
short-lived, and a receipt that waits in a queue will hit it. Run it deliberately.

The download is a genuine two-hop fetch. The adapter first resolves the media id against the Graph
API, reads `url` and `mime_type` from the JSON, then downloads that URL with an explicit `Bearer`
header — the media host does not share the Graph client's default headers. If the first hop succeeds
and the second fails, you get `document_unreadable`, not `not_found`; that distinction tells you which
hop broke.

Note that `mime_type` from the lookup wins over whatever the envelope claimed. It does not matter
much, because `DocumentProcessor` decides the real type from magic bytes anyway, but it explains why
the logged type may differ from what you sent.

## Exercising the same code without WhatsApp credentials

Everything except the two live hops can be driven through the tool API:

```bash
TOOL_SECRET=$(grep JUSTINA_TOOL_SECRET .env | cut -d= -f2)

curl -s -X POST http://localhost:8080/tools/session.context \
  -H "Content-Type: application/json" \
  -H "X-Justina-Tool-Key: $TOOL_SECRET" \
  -d '{"envelope":{"channel":"whatsapp","userId":"6591234567","conversationId":"6591234567"}}'
```

To test the adapter without Meta, point `WhatsApp:GraphBaseUrl` at a local stub that answers the media
lookup with `{"url":"http://stub/file.pdf","mime_type":"application/pdf"}` and serves the file. That
covers both hops, the bearer header and the error mapping, without an access token.

Response conventions are the same everywhere: refusals are HTTP 200 with `"ok": false` and a code and
message the agent relays; `unauthorized` is HTTP 403; missing or wrong `X-Justina-Tool-Key` is HTTP
401; `GET` on a tool path is 405.

## What cannot be tested here

- **A live WhatsApp round trip** needs a Meta app, a verified number and a permanent token. None of it
  is covered by the automated suite.
- **Signature verification** is not implemented in C# and unproven in OpenClaw. W-SEC-01 and W-SEC-02
  above are the only way to find out. Record what you actually observe.
- **Template messages and the 24-hour session window** are not modelled anywhere in Justina. The
  responder sends plain text only. If your test account falls outside the customer service window,
  replies will silently fail with `external_api_failed` and the cause will not be obvious from
  Justina's logs — check the Graph API error in the OpenClaw container.
- **Whether OpenClaw's WhatsApp plugin passes a stable `messageId`** determines whether W7 protects
  you in production. Confirm from the OpenClaw logs what it actually sends.
