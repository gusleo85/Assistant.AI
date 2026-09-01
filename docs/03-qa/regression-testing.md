# Regression Testing

What to re-run after a change, and how much of it depends on what was touched.

## The always-run set

Run this after every change, no exceptions. It takes well under a minute and needs no network, no
Docker and no credentials.

```bash
dotnet build Justina.slnx
for p in tests/*/; do dotnet test "$p" --nologo -v q; done
```

Baseline to compare against:

| Project | Tests | Expected |
|---|---|---|
| `Justina.ArchitectureTests` | 20 | Passed |
| `Justina.Core.UnitTests` | 51 | Passed |
| `Justina.Expense.UnitTests` | 75 | Passed |
| `Justina.IntegrationTests` | 10 | Passed |
| `Justina.Recruitment.UnitTests` | 7 | Passed |
| **Total** | **163** | **0 failed, 0 skipped** |

The build must produce **0 warnings and 0 errors**. `Directory.Build.props` sets
`TreatWarningsAsErrors`, so a warning is a build failure, not a nag.

If the test count drops, that is a regression even when everything reported passes. A deleted or
skipped test is a lost guarantee. Compare the total, not just the colour.

If `grep` reports binary output, pipe through `tr -d '\000'`:

```bash
dotnet test tests/Justina.Expense.UnitTests --nologo -v q 2>&1 | tr -d '\000' | grep -E "Passed|Failed"
```

Also run, at least before any release:

```bash
dotnet list Justina.slnx package --vulnerable --include-transitive
docker compose config
```

Expected: no vulnerable packages across all 15 projects, and `docker compose config` exiting 0 with 5
services, 1 network and 3 volumes.

## Per-area checklist

Match what changed to the column on the left and run everything in the right-hand column on top of the
always-run set.

### `src/Justina.Expense.Domain/` — the receipt state machine

The correctness core. A change here can silently allow an illegal transition.

| Re-run | Why |
|---|---|
| `tests/Justina.Expense.UnitTests` in full | Every legal and illegal transition lives here |
| Manual: [`receipt-testing.md`](receipt-testing.md) full journey | Extract, display, edit, re-display, confirm |
| Manual: cancel path | Business rule 5 — nothing must be submitted |
| Manual: duplicate confirmation | Business rule 6 — exactly one expense |
| Manual: multi-receipt PDF | Business rule 10 — no silent merging |

Specifically confirm these still pass by name, because they encode the rules rather than the code:

- `Confirming_twice_is_rejected_by_the_state_machine`
- `Editing_after_confirmation_is_rejected`
- `Cancel_is_rejected_after_submission`
- `Confirm_is_rejected_before_extraction_completes`
- `Submission_cannot_start_before_confirmation`
- `Edit_changes_only_the_requested_field_and_stays_awaiting_confirmation`
- `A_batch_creates_independent_receipts_that_share_a_batch_id`

### `src/Justina.Expense.Application/Receipts/` — normalizer, translator, submission

| Re-run | Why |
|---|---|
| `tests/Justina.Expense.UnitTests` in full | Amount and date parsing, field synonyms, idempotency keys |
| `tests/Justina.IntegrationTests` | The submission payload shape is asserted against the stub |
| Manual: an edit in each supported form | `amount should be 15.50`, `merchant is X`, `date should be August 30`, `currency should be IDR`, `gst is 1.03` |
| Manual: an edit that must be refused | An invalid currency, a negative amount, an unknown field |

If `BuildIdempotencyKey` changed, re-check both of these explicitly:

- `The_idempotency_key_is_stable_for_the_same_receipt_content`
- `Two_different_receipts_do_not_share_an_idempotency_key`

A key that is no longer stable means a retry creates a second expense. That is the single worst
regression this product can have.

### `src/Justina.Core.Infrastructure/Documents/` — document processing

| Re-run | Why |
|---|---|
| `tests/Justina.Core.UnitTests` in full | Sniffing, size caps, page caps, classification, rasterization |
| Manual: [`pdf-testing.md`](pdf-testing.md) in full | Text PDF, scanned PDF, multi-page, multi-receipt |
| Manual: JPEG, PNG and WEBP intake | Each image format |
| Manual: corrupt, oversized, too-many-pages, disguised-type | The rejection paths and their error codes |

Watch the error codes specifically. A change that turns `document_unreadable` into an unhandled
exception is a user-visible regression even if nothing else breaks: `unsupported_media`,
`media_too_large`, `document_unreadable`, `too_many_pages`.

### `src/Justina.Core.Infrastructure/Vision/` — the OpenAI provider

| Re-run | Why |
|---|---|
| `tests/Justina.Core.UnitTests` | Nothing here covers the provider directly, but the document shapes it consumes are covered |
| Manual: extraction on every fixture | There is no offline provider test; this is the only coverage |
| Manual: [`security-testing.md`](security-testing.md) prompt-injection case | Document content must stay data, never instruction |
| Manual: provider failure | Unset `OPENAI_API_KEY` and confirm `vision_failed` with a friendly message and no provider detail |

If the extraction schema in `ReceiptExtractionSchema.cs` changed, re-run the whole of
[`pdf-testing.md`](pdf-testing.md). The schema's top level is a **list** of receipts; if that ever
becomes a single object, business rule 10 breaks silently.

### `src/Justina.Expense.Infrastructure/Api/ExpenseApiClient.cs`

| Re-run | Why |
|---|---|
| `tests/Justina.IntegrationTests` in full | All ten cases target this class |
| Manual: [`api-testing.md`](api-testing.md) | Timeout, 5xx, invalid response, duplicate submit |

Check the header assertions survive: `Authorization`, `Idempotency-Key`, `X-Correlation-Id`. The test
that covers them is `The_request_carries_the_credentials_the_idempotency_key_and_the_correlation_id`.

Also confirm `A_server_failure_is_retryable_and_never_leaks_provider_detail_to_the_user` still asserts
that the provider's body does not reach the user-facing message.

Note the standing gap: the tests construct a bare `HttpClient`, so the Polly resilience handler wired in
`AddExpenseInfrastructure` is bypassed. Retry and circuit-breaker behaviour is not covered by any test
and must be checked by hand if it changes.

### `src/Justina.Core.Application/Messaging/` — the CQRS pipeline and decorators

| Re-run | Why |
|---|---|
| `tests/Justina.Core.UnitTests` | Authorization and idempotency decorators |
| `tests/Justina.ArchitectureTests` | Layering |
| Manual: unauthorized user journey | Business rule 7 |
| Manual: a repeated `confirm_receipt` | The idempotency decorator's replay path |

The decorator order is `Logging → Authorization → Validation → Idempotency → handler`. If
`HandlerRegistration.cs` changed, verify authorization still sits **outside** validation — a refused
caller must not learn the shape of the request they were refused.

Confirm `A_failed_command_is_not_stored` still passes. Caching a failure would make a transient error
permanent.

### `src/Justina.Core.Infrastructure/Channels/` — Telegram or WhatsApp adapters

| Re-run | Why |
|---|---|
| Manual: [`telegram-testing.md`](telegram-testing.md) | Text, image, PDF, edit, confirm, cancel |
| Manual: [`whatsapp-testing.md`](whatsapp-testing.md) | The same six |
| Manual: unconfigured channel | Blank the token, confirm `not_available` rather than a crash |

Both adapters must produce the same `DownloadedMedia` shape. If one changes, check the other still
behaves identically through the shared document pipeline — that equivalence is the whole point of the
abstraction.

### `src/Justina.Api/Tools/` — the tool endpoints

| Re-run | Why |
|---|---|
| Manual: every endpoint with curl | See [`test-environment.md`](test-environment.md), section 4 |
| Manual: the auth cases | Missing key, wrong key, correct key |
| Manual: envelope validation | Unsupported channel, empty `userId` |

Quick smoke, no database needed:

```bash
BASE=http://127.0.0.1:5199
KEY=a-test-secret

# 401 expected
curl -s -o /dev/null -w "no key: %{http_code}\n" -X POST $BASE/tools/session.context \
  -H "Content-Type: application/json" -d '{"envelope":{"channel":"telegram","userId":"u","conversationId":"c"}}'

# 401 expected
curl -s -o /dev/null -w "bad key: %{http_code}\n" -X POST $BASE/tools/session.context \
  -H "Content-Type: application/json" -H "X-Justina-Tool-Key: wrong" \
  -d '{"envelope":{"channel":"telegram","userId":"u","conversationId":"c"}}'

# 200 with ok:false and validation_failed expected
curl -s -X POST $BASE/tools/session.context \
  -H "Content-Type: application/json" -H "X-Justina-Tool-Key: $KEY" \
  -d '{"envelope":{"channel":"discord","userId":"u","conversationId":"c"}}'

# 405 expected
curl -s -o /dev/null -w "GET: %{http_code}\n" $BASE/tools/session.context -H "X-Justina-Tool-Key: $KEY"
```

Also verify refusals still come back as HTTP 200 with `ok: false`, and that `unauthorized` alone comes
back as HTTP 403. If a refusal starts arriving as a 4xx or 5xx, the agent will treat a business decision
as a transport failure and may retry it.

### `src/Justina.Api/Program.cs` or `Directory.Build.props`

| Re-run | Why |
|---|---|
| Everything | These affect the whole application |
| Manual: `docker compose up` end to end | Startup, migration, health |
| Manual: both health endpoints | Defect B2 surfaces here: `/health/live` includes the database check |

If `Directory.Build.props` changed, check the produced runtimeconfig:

```bash
cat src/Justina.Api/bin/Debug/net10.0/Justina.Api.runtimeconfig.json
```

`System.Globalization.Invariant` must be `false` or absent. If it is `true`,
`Microsoft.Data.SqlClient` refuses every connection with
`System.NotSupportedException: Globalization Invariant Mode is not supported.` and the app dies at
startup. That was blocker B1, now fixed — do not let it regress. See
[`test-environment.md`](test-environment.md).

### EF configuration or migrations

| Re-run | Why |
|---|---|
| Manual: `docker compose down -v && docker compose up -d` | A migration must apply to an empty database |
| Manual: re-seed `Principals` and repeat a full journey | Schema changes break seeded data |

Verify these indexes survive — each one is load-bearing:

| Index | Protects |
|---|---|
| `UX_Receipts_ExternalExpenseId` (filtered, `WHERE [ExternalExpenseId] IS NOT NULL`) | One expense per receipt. The filter matters: SQL Server treats multiple NULLs as duplicates |
| `PK_InboundMessages (Channel, MessageId)` | Webhook retries do not create a second receipt |
| `UX_Conversations_Channel_ExternalConversationId` | One conversation row per chat |
| `UX_Principals_Channel_UserId` | One principal per channel identity |
| `Receipts.RowVersion` (`rowversion`) | Two concurrent confirmations cannot both win |

### `docker/`, `docker-compose.yml`, `.env.example`

| Re-run | Why |
|---|---|
| `docker compose config` | Must exit 0 |
| `docker compose config \| grep localhost` | Only container self-checks may appear |
| nginx syntax check | See below |
| Manual: full stack startup | Service-name resolution, health, ordering |

```bash
docker run --rm \
  --add-host justina-openclaw:127.0.0.1 --add-host justina-app:127.0.0.1 \
  -v "$PWD/docker/nginx/nginx.conf:/etc/nginx/nginx.conf:ro" \
  -v "$PWD/docker/nginx/conf.d:/etc/nginx/conf.d:ro" \
  nginx:1.27-alpine nginx -t
```

Expected: `syntax is ok` / `test is successful`.

If a new environment variable was added to `docker-compose.yml`, it must also appear in `.env.example`
with no real value. Check both.

### `docker/openclaw/agents/*.md` — agent prompts

Prompt changes are not covered by any automated test. The only regression net is manual.

| Re-run | Why |
|---|---|
| Manual: [`agent-routing-testing.md`](agent-routing-testing.md) in full | Routing is probabilistic; run the whole prompt set |
| Manual: the confirmation discipline cases | The agent must never call `confirm_receipt` before an explicit yes |
| Manual: [`security-testing.md`](security-testing.md) injection cases | The prompts carry the injection defence |

Run routing prompts more than once. A single pass proves nothing about an LLM. Three runs of the same
prompt giving three different routes is itself the finding.

## The manual smoke journey

The shortest sequence that proves the product still works end to end. Run this before signing off any
release. It needs the full Docker stack, a seeded principal, and a Telegram bot.

1. Send `hello` — the assistant replies and does not start a workflow.
   `justina.session.context` reports `activeWorkflow: null`.
2. Send `receipt.jpg` — the assistant shows merchant, date, currency, amount and asks if it is correct.
   **No Expense API call has happened yet.**
3. Send `amount should be 15.50` — only the amount changes. The complete receipt is shown again and
   confirmation is asked again.
4. Send `no, cancel` — the assistant confirms nothing was submitted. The stub's request log is empty.
5. Send `receipt.jpg` again — a new receipt is extracted and displayed.
6. Send `yes` — the receipt is submitted and an expense reference is returned.
7. Send `yes` again — the assistant reports the same reference. The stub shows **exactly one** POST.
8. Send `find me senior .NET candidates` — the reply says recruitment search is not connected yet. No
   Expense API call occurs.
9. Send `create a report` — the assistant asks a clarifying question rather than guessing.

Nine steps. If all nine behave, the product's core promises hold. If step 7 produces two POSTs, stop and
escalate — that is the one failure that costs real money.

## Recording results

Every regression pass goes into `test/test-report.md` in the standard shape:

```
Test Case
Expected Result
Actual Result
Status
Evidence
```

Rules that are not negotiable:

- **Never record a result you did not observe.** Anything you could not run is written verbatim as:

  ```
  NOT TESTED
  Reason: ...
  ```

- **Evidence means evidence.** Command output, an HTTP status and body, a log line, a database row, a
  screenshot of the chat. "Looks fine" is not evidence.
- **A previously failed test is never marked Passed without a fresh run.** Fix, re-run, observe, then
  record.
- Put scope limitations at the top of the report, not in a footnote.
- End with exactly one of `TEST STATUS: PASSED` or `TEST STATUS: FAILED`.

When a case fails, record it, report it to the developer, and re-verify after the fix. Loop until it
genuinely passes.

## Known standing gaps

These are not regressions — they are things that have never been covered, so no regression run will
catch a change in them:

- No CI pipeline. Everything here is run by hand.
- `tests/fixtures/` does not exist; the tester supplies every document.
- No database integration tests. The command pipeline has never run against a real SQL Server.
- Retry and circuit-breaker behaviour on `ExpenseApiClient` is untested.
- No test covers `OpenAiVisionProvider` directly.
- No log redactor exists, so nothing verifies that secrets stay out of logs.
- The repository has no git commits, so there is no baseline to diff against.
