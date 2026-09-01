# API Testing

Justina has two API surfaces, and they fail in different ways. Test them separately.

| Surface | Direction | Who calls it | Where the code lives |
|---|---|---|---|
| Tool API | Inbound | OpenClaw agents | `src/Justina.Api/Tools/ToolEndpoints.cs` |
| Expense API client | Outbound | `justina-app` | `src/Justina.Expense.Infrastructure/Api/ExpenseApiClient.cs` |

Related: [test-environment.md](test-environment.md) for setup, [security-testing.md](security-testing.md)
for the adversarial cases, [receipt-testing.md](receipt-testing.md) for the journey the Tool API drives.

## Precondition: blocker B1

`Directory.Build.props` sets `<InvariantGlobalization>true</InvariantGlobalization>`.
`Microsoft.Data.SqlClient` refuses to open a connection in that mode and throws:

```
System.NotSupportedException: Globalization Invariant Mode is not supported.
   at Microsoft.Data.SqlClient.SqlConnection.TryOpen(...)
```

`justina-app` exits at startup because the migration step throws (`Program.cs` line 87). Causation was
confirmed by flipping only that flag in a copy of the build output: the same binaries then made a real TCP
attempt and failed with an ordinary `SqlException`.

Everything below that touches the database is blocked until B1 is fixed. The parts that do **not** touch
the database still run today: the key middleware, envelope validation, the HTTP method surface, and every
Expense API client test.

---

# Part A — the inbound Tool API

## The surface

`POST` only. `GET` on a tool route returns **405**.

| Route | Purpose |
|---|---|
| `/tools/session.context` | Who the user is, what they may do, whether a workflow is active |
| `/tools/expense.receive_media` | Register an image or PDF and extract it |
| `/tools/expense.get_receipt` | Read the current receipt |
| `/tools/expense.edit_receipt` | Apply field edits |
| `/tools/expense.confirm_receipt` | Confirm and submit |
| `/tools/expense.cancel_receipt` | Discard the receipt in progress |
| `/tools/recruitment.search_candidates` | Candidate search (phase 1: always unavailable) |
| `/health/live` | Health |
| `/health/ready` | Health |

There is nothing else. Any other path is a 404 from ASP.NET Core routing.

## Refusal versus error

This convention matters, and testers get it wrong on the first pass.

| Outcome | HTTP | Body |
|---|---|---|
| Success | 200 | `{"ok":true,"data":{...}}` |
| Business refusal | 200 | `{"ok":false,"error":{"code":"...","message":"..."}}` |
| Authorization refusal | 403 | `{"ok":false,"error":{"code":"unauthorized",...}}` |
| Bad or missing tool key | 401 | empty |
| Tool key not configured on the server | 503 | empty |
| Wrong HTTP method | 405 | empty |

A refusal is a **successful HTTP call**. The agent is meant to read the reason and relay it to the user.
If you see a 500, that is a defect, not a refusal.

Error codes come from `src/Justina.Core.Domain/Results/Error.cs`:

```
validation_failed   not_found            invalid_workflow_state   unauthorized
conflict            unsupported_media    media_too_large          document_unreadable
too_many_pages      vision_failed        external_api_failed      not_available
```

## The envelope

Every request body carries an `envelope`. The agent supplies identity **claims**; C# decides what they mean.

```json
{
  "envelope": {
    "channel": "telegram",
    "userId": "123456789",
    "conversationId": "123456789",
    "messageId": "42",
    "correlationId": "optional-trace-id"
  }
}
```

`channel` must be `telegram` or `whatsapp`. `userId` and `conversationId` are required. `messageId` and
`correlationId` are optional; omitting `correlationId` generates one.

## Setup for these tests

```bash
export TOOL_KEY="the value of JUSTINA_TOOL_SECRET from your .env"
export BASE="http://localhost:8080"
```

Inside the compose network the address is `http://justina-app:8080`. From the host you need a published
port; compose does not publish one, so either add a temporary port mapping in an override file or run the
app directly as described in [test-environment.md](test-environment.md).

## A1 — Authentication

### A1.1 No key

```bash
curl -s -o /dev/null -w '%{http_code}\n' -X POST "$BASE/tools/session.context" \
  -H 'Content-Type: application/json' \
  -d '{"envelope":{"channel":"telegram","userId":"u1","conversationId":"c1"}}'
```

Expected: `401`. Confirmed on a running instance.

### A1.2 Wrong key

Same call with `-H "X-Justina-Tool-Key: wrong"`. Expected: `401`.

The comparison uses `CryptographicOperations.FixedTimeEquals`, so a wrong key cannot be discovered by
timing. You cannot usefully test that with curl; it is a code property. Do not try to time it.

### A1.3 Correct key

Same call with `-H "X-Justina-Tool-Key: $TOOL_KEY"`. Expected: the middleware passes and you get a
200 with either data or a refusal — not a 401.

### A1.4 Server with no secret configured

Start the app with `ToolApi__SharedSecret` empty.

```bash
ToolApi__SharedSecret="" ASPNETCORE_URLS=http://127.0.0.1:5109 dotnet run --project src/Justina.Api
```

Expected: every `/tools` call returns **503**, and the log carries:

```
The tool API shared secret is not configured; refusing every tool call
```

This is the fail-closed behaviour. A pass here means a misconfigured deployment refuses work rather than
accepting anonymous work.

### A1.5 Health endpoints need no key

```bash
curl -s -o /dev/null -w '%{http_code}\n' "$BASE/health/live"
```

Expected: `200` when the database is reachable, `503` when it is not. Never 401 — the middleware only
guards paths starting `/tools`.

Note the defect: `/health/live` and `/health/ready` are registered identically with no predicate, so
liveness includes the database check. See [security-testing.md](security-testing.md), finding B2.

### A1.6 Wrong method

```bash
curl -s -o /dev/null -w '%{http_code}\n' "$BASE/tools/session.context" \
  -H "X-Justina-Tool-Key: $TOOL_KEY"
```

Expected: `405`. Confirmed on a running instance.

## A2 — Envelope validation

These run before any database access, so they work even under B1.

### A2.1 Unsupported channel

```bash
curl -s -X POST "$BASE/tools/session.context" \
  -H "X-Justina-Tool-Key: $TOOL_KEY" -H 'Content-Type: application/json' \
  -d '{"envelope":{"channel":"discord","userId":"u1","conversationId":"c1"}}'
```

Expected, exactly:

```json
{"ok":false,"error":{"code":"validation_failed","message":"'discord' is not a supported channel."}}
```

HTTP 200. Confirmed on a running instance.

### A2.2 Missing user id

```bash
curl -s -X POST "$BASE/tools/session.context" \
  -H "X-Justina-Tool-Key: $TOOL_KEY" -H 'Content-Type: application/json' \
  -d '{"envelope":{"channel":"telegram","userId":"","conversationId":"c1"}}'
```

Expected, exactly:

```json
{"ok":false,"error":{"code":"validation_failed","message":"The request envelope needs a user id and a conversation id."}}
```

HTTP 200. Confirmed on a running instance. The same message appears for a missing `conversationId`.

### A2.3 Missing envelope

Send `{}`. Expected: `validation_failed`, "The request envelope is missing."

## A3 — Authorization

Authentication proves the caller is OpenClaw. Authorization decides what the **user behind the message**
may do. They are independent: a valid tool key gets you in the door and nothing more.

`RequestContextFactory` resolves `channel` + `userId` against the `Principals` table through
`AuthorizationService`. An unmapped user becomes `UserContext.Anonymous`, which holds no capabilities.
`AuthorizationCommandDecorator` and `AuthorizationQueryDecorator` then refuse anything that declares a
required capability — **before validation**, so a refused caller learns nothing about the request shape.

| Capability | Grants |
|---|---|
| `expense.submit` | receive media, extract, edit, confirm, cancel, submit |
| `expense.read` | get receipt, get receipt status |
| `recruitment.search` | candidate search |

Pipeline order: Logging → Authorization → Validation → Idempotency → handler.

### A3.1 Unmapped user is refused

Use a `userId` that has no row in `Principals`.

```bash
curl -s -w '\nHTTP=%{http_code}\n' -X POST "$BASE/tools/expense.confirm_receipt" \
  -H "X-Justina-Tool-Key: $TOOL_KEY" -H 'Content-Type: application/json' \
  -d '{"envelope":{"channel":"telegram","userId":"nobody","conversationId":"c1"}}'
```

Expected: HTTP **403**, code `unauthorized`, message "You are not authorized to perform this action."

### A3.2 Mapped user without the capability

Seed a principal with `["expense.read"]` only, then call `expense.confirm_receipt`.

Expected: HTTP 403, `unauthorized`. The handler never runs.

### A3.3 A query refusal has its own wording

Call `expense.get_receipt` as a user without `expense.read`.

Expected: HTTP 403, `unauthorized`, message "You are not authorized to view this."

### A3.4 The agent cannot argue its way past it

Repeat A3.1 several times, and vary the wording of anything the agent might send. The refusal must be
byte-identical every time. Authorization is a C# decision; there is no prompt that changes it.

### A3.5 Authorization precedes validation

Call `expense.edit_receipt` as an unauthorized user with a deliberately malformed `edits` array.

Expected: `unauthorized`, **not** `validation_failed`. If you get a validation error, the decorator order
has regressed and an unauthorized caller is learning the request shape.

Six unit tests in `tests/Justina.Core.UnitTests/DecoratorTests.cs` already assert this behaviour,
including that the handler is never invoked. They pass. A3 is the end-to-end confirmation.

## A4 — Endpoint walkthrough

All examples assume `TOOL_KEY` and `BASE` are exported and the user is seeded with `expense.submit`
and `expense.read`.

### session.context

```bash
curl -s -X POST "$BASE/tools/session.context" \
  -H "X-Justina-Tool-Key: $TOOL_KEY" -H 'Content-Type: application/json' \
  -d '{"envelope":{"channel":"telegram","userId":"u1","conversationId":"c1"}}'
```

Returns `channel`, `conversationId`, `isAuthenticated`, `displayName`, `capabilities`, `activeWorkflow`,
`activeEntityId`. With no receipt in progress, `activeWorkflow` is null. During a receipt workflow it is
`expense.receipt`.

### expense.receive_media

```bash
curl -s -X POST "$BASE/tools/expense.receive_media" \
  -H "X-Justina-Tool-Key: $TOOL_KEY" -H 'Content-Type: application/json' \
  -d '{
        "envelope":{"channel":"telegram","userId":"u1","conversationId":"c1","messageId":"m-1"},
        "media":{"mediaId":"file-id-from-telegram","mimeType":"image/jpeg","fileName":"receipt.jpg","sizeBytes":48210}
      }'
```

Returns `receiptCount`, `batchId`, `receipts[]`. Sending the same `messageId` twice must return the
existing receipt, not create a second one — see [receipt-testing.md](receipt-testing.md).

Omitting `media` or sending a blank `mediaId` returns `validation_failed`, "No media reference was
supplied."

### expense.get_receipt

```bash
curl -s -X POST "$BASE/tools/expense.get_receipt" \
  -H "X-Justina-Tool-Key: $TOOL_KEY" -H 'Content-Type: application/json' \
  -d '{"envelope":{"channel":"telegram","userId":"u1","conversationId":"c1"}}'
```

Omit `receiptId` to read the conversation's active receipt. With nothing in progress: `not_found`,
"There is no receipt in progress."

### expense.edit_receipt

```bash
curl -s -X POST "$BASE/tools/expense.edit_receipt" \
  -H "X-Justina-Tool-Key: $TOOL_KEY" -H 'Content-Type: application/json' \
  -d '{
        "envelope":{"channel":"telegram","userId":"u1","conversationId":"c1"},
        "edits":[{"field":"amount","value":"15.50"}]
      }'
```

Returns the full updated receipt. Editing a receipt that is not `WaitingConfirmation` returns
`invalid_workflow_state`.

### expense.confirm_receipt

```bash
curl -s -X POST "$BASE/tools/expense.confirm_receipt" \
  -H "X-Justina-Tool-Key: $TOOL_KEY" -H 'Content-Type: application/json' \
  -d '{"envelope":{"channel":"telegram","userId":"u1","conversationId":"c1"}}'
```

This is the only path to the Expense API. See A6 for the duplicate-submission test.

### expense.cancel_receipt

```bash
curl -s -X POST "$BASE/tools/expense.cancel_receipt" \
  -H "X-Justina-Tool-Key: $TOOL_KEY" -H 'Content-Type: application/json' \
  -d '{"envelope":{"channel":"telegram","userId":"u1","conversationId":"c1"}}'
```

Cancelling an already-cancelled receipt succeeds and returns the same snapshot. Cancelling a submitted
receipt returns `invalid_workflow_state`, "This receipt has already been submitted and cannot be
cancelled."

### recruitment.search_candidates

```bash
curl -s -X POST "$BASE/tools/recruitment.search_candidates" \
  -H "X-Justina-Tool-Key: $TOOL_KEY" -H 'Content-Type: application/json' \
  -d '{
        "envelope":{"channel":"telegram","userId":"u1","conversationId":"c1"},
        "role":"Senior .NET Developer","skills":["C#","Azure"]
      }'
```

Expected today, with `recruitment.search` granted: `not_available`, "Recruitment search is not connected
yet, so I cannot run that search." Phase 1 is routing only (plan risk R2).

With no criteria at all and a configured API, the handler returns `validation_failed`, "Tell me a role, a
skill, a seniority or a location to search for." With no API configured, the unavailability check comes
first, so you will see `not_available` regardless.

The point of this test is negative: confirm it reports honestly rather than inventing candidates, and
confirm no Expense API call happens. See [agent-routing-testing.md](agent-routing-testing.md).

---

# Part B — the outbound Expense API client

## The provisional contract

**The real Expense API specification has not been supplied (plan risk R1).** What exists is a documented
assumption. When the specification arrives, the mapping in `BuildPayload` and `ReadExpenseId` changes and
these expectations move with it. Everything above the client — validation, state machine, idempotency,
authorization — is unaffected.

Request: `POST {BaseUrl}/{SubmitPath}`, default path `expenses`.

```json
{
  "merchant": "Starbucks",
  "date": "2026-08-31",
  "currency": "SGD",
  "amount": 12.50,
  "category": "Meals",
  "receiptNumber": "INV-12345",
  "taxAmount": 1.03,
  "submittedBy": "u1",
  "lineItems": [
    {"description":"Latte","quantity":1,"unitPrice":12.50,"amount":12.50}
  ]
}
```

Headers:

| Header | Value | Configurable via |
|---|---|---|
| `Authorization` | `Bearer <key>` | `ExpenseApi:ApiKeyHeader`, `ExpenseApi:ApiKeyPrefix` |
| `Idempotency-Key` | SHA-256 digest of the receipt's identity and content | `ExpenseApi:IdempotencyHeader` |
| `X-Correlation-Id` | Propagated from the inbound message | `ExpenseApi:CorrelationHeader` |

The expense id is read from `id`, then `expenseId`, then `data.id`, in that order.

## B1 — Status mapping

Every row below is asserted by a passing integration test in
`tests/Justina.IntegrationTests/ExpenseApiClientTests.cs`.

| Stub response | Error code | User-facing message |
|---|---|---|
| 2xx with `{"id":"EXP-9001"}` | success | — |
| 401 or 403 | `unauthorized` | The expense system refused this submission for this user. |
| 409 | `conflict` | The expense system reports this expense already exists. |
| 400 or 422 | `validation_failed` | The expense system rejected these details. Please check them and try again. |
| any other non-2xx | `external_api_failed` | The expense system could not accept the receipt. It can be retried. |
| timeout | `external_api_failed` | The expense system did not respond in time. Your receipt is saved and can be retried. |
| unreachable host | `external_api_failed` | I could not reach the expense system. Your receipt is saved and can be retried. |
| 2xx, no readable id | `external_api_failed` | The expense system accepted the receipt but did not return a reference. |
| 2xx, unparseable body | `external_api_failed` | (as above) |
| blank `BaseUrl` | `not_available` | Expense submission is not available right now. |

Run them:

```bash
dotnet test tests/Justina.IntegrationTests --nologo
```

Expected: 10 passed, 0 failed. These do not need Docker, a database, or network access — WireMock runs
in-process. They pass today and are unaffected by B1.

## B2 — What the tests do not cover

The integration tests construct a plain `HttpClient`:

```csharp
var httpClient = new HttpClient { BaseAddress = ..., Timeout = ... };
```

The retry policy and circuit breaker are registered on the DI-built client in
`src/Justina.Expense.Infrastructure/DependencyInjection.cs`:

```csharp
.AddStandardResilienceHandler();
```

That handler is where retry, circuit breaking and the per-attempt timeout live. **No automated test
exercises it.** Treat retry and circuit-breaker behaviour as unverified until someone tests it manually.

### B2.1 Observing retries by hand

Stand up a stub that fails twice and then succeeds, and watch the request log.

```bash
docker run --rm -p 9090:8080 wiremock/wiremock:3.9.1
```

Create a scenario:

```bash
curl -s -X POST http://localhost:9090/__admin/mappings -d '{
  "scenarioName": "flaky", "requiredScenarioState": "Started", "newScenarioState": "one",
  "request": {"method":"POST","url":"/expenses"},
  "response": {"status": 503}
}'
curl -s -X POST http://localhost:9090/__admin/mappings -d '{
  "scenarioName": "flaky", "requiredScenarioState": "one", "newScenarioState": "two",
  "request": {"method":"POST","url":"/expenses"},
  "response": {"status": 503}
}'
curl -s -X POST http://localhost:9090/__admin/mappings -d '{
  "scenarioName": "flaky", "requiredScenarioState": "two",
  "request": {"method":"POST","url":"/expenses"},
  "response": {"status": 200, "body": "{\"id\":\"EXP-RETRY-1\"}", "headers":{"Content-Type":"application/json"}}
}'
```

Point the app at it and confirm one receipt:

```bash
export ExpenseApi__BaseUrl="http://localhost:9090"
export ExpenseApi__ApiKey="stub-key"
```

Confirm a receipt, then read the stub's request log:

```bash
curl -s http://localhost:9090/__admin/requests | jq '.requests | length'
```

Pass: three requests recorded, all carrying the **same** `Idempotency-Key`, and the receipt ends in
`Submitted` with `ExternalExpenseId = EXP-RETRY-1`.

Fail: the idempotency key differs between attempts. That would mean a retry could create a second expense
on a server that keys on the header.

### B2.2 Circuit breaker

Force sustained 500s and keep confirming receipts. After enough consecutive failures the handler should
short-circuit and fail fast without reaching the stub. Compare the app's request count against the stub's
request log: once the breaker opens, the stub stops receiving requests while the app keeps returning
`external_api_failed`.

This is a timing-dependent test. Record the thresholds you observe rather than asserting a specific
number — the standard handler's defaults may change with the package version.

## B3 — Timeout

`ExpenseApi:TimeoutSeconds` (default 30) sets `HttpClient.Timeout`. The standard resilience handler adds
its own per-attempt timeout.

Configure a stub that delays past the timeout:

```bash
curl -s -X POST http://localhost:9090/__admin/mappings -d '{
  "request": {"method":"POST","url":"/expenses"},
  "response": {"status": 200, "fixedDelayMilliseconds": 45000}
}'
```

Expected: `external_api_failed`, message containing "did not respond in time" and "retried". The receipt
must be in `SubmissionFailed`, not lost, and must be retryable — `BeginSubmission` is legal again from
`SubmissionFailed`.

Check the database:

```sql
SELECT Id, State, FailureReason, ExternalExpenseId FROM Receipts WHERE Id = '<receipt id>';
```

Pass: `State = 7` (`SubmissionFailed`), `ExternalExpenseId` null.

Reduce `ExpenseApi__TimeoutSeconds` to 3 to keep this test fast.

## B4 — Provider detail must not reach the user

Stub a 500 with a revealing body:

```bash
curl -s -X POST http://localhost:9090/__admin/mappings -d '{
  "request": {"method":"POST","url":"/expenses"},
  "response": {"status": 500, "body": "java.lang.NullPointerException at com.acme.expense.Ledger:441"}
}'
```

Expected: the user-facing message is "The expense system could not accept the receipt. It can be
retried." and nothing else. The stack trace appears in the app log, truncated to 500 characters, and never
in the tool response.

Read the tool response and confirm the string `NullPointerException` does not appear anywhere in it.

## B5 — Unconfigured API

Leave `EXPENSE_API_URL` blank, which is the current default in `.env.example`.

Expected: `not_available`, "Expense submission is not available right now." and **no HTTP call at all**.
Confirm with an empty stub request log. This is the behaviour a tester will hit by default today, because
the real contract has not been supplied.

## B6 — Exactly one expense on a duplicate confirmation

This is the acceptance criterion that matters most, and it has four independent defences. Test the
outcome, then verify each defence held.

Setup: a stub that records every request and always returns a new id.

```bash
curl -s -X POST http://localhost:9090/__admin/mappings -d '{
  "request": {"method":"POST","url":"/expenses"},
  "response": {"status": 200, "body": "{\"id\":\"EXP-DUP-1\"}", "headers":{"Content-Type":"application/json"}}
}'
curl -s -X DELETE http://localhost:9090/__admin/requests
```

Steps:

1. Drive a receipt to `WaitingConfirmation`.
2. Call `expense.confirm_receipt`.
3. Call `expense.confirm_receipt` again with the identical body.

Expected:

- Both calls return HTTP 200 with `ok:true` and the **same** `externalExpenseId`.
- The stub recorded exactly **one** `POST /expenses`:

```bash
curl -s http://localhost:9090/__admin/requests | jq '[.requests[] | select(.request.url == "/expenses")] | length'
```

Pass: `1`. Fail: `2` — that is a duplicate expense and a blocking defect.

The four defences, any one of which alone is enough:

| Defence | Where |
|---|---|
| `ConfirmReceiptCommand` is idempotent on key `confirm:<receiptId>` | `IdempotencyCommandDecorator` |
| A receipt already in `Submitted` short-circuits before the API is touched | `ReceiptSubmissionService` |
| `Confirm` is legal only from `WaitingConfirmation` | `Receipt` state machine |
| Filtered unique index `UX_Receipts_ExternalExpenseId` | SQL Server |

Also run the concurrent variant: fire both confirmations at once.

```bash
curl -s -X POST "$BASE/tools/expense.confirm_receipt" -H "X-Justina-Tool-Key: $TOOL_KEY" \
  -H 'Content-Type: application/json' -d "$BODY" &
curl -s -X POST "$BASE/tools/expense.confirm_receipt" -H "X-Justina-Tool-Key: $TOOL_KEY" \
  -H 'Content-Type: application/json' -d "$BODY" &
wait
```

Expected: one succeeds; the other either returns the same expense id or returns `conflict`, "Someone else
changed this at the same time. Please check the current state and try again." Never two expenses. The
`rowversion` column on `Receipts` is what makes the losing writer fail.

## B7 — Idempotency key stability

The key is a SHA-256 hex digest over the receipt id, merchant, date, currency, amount, receipt number and
tax. Two unit tests in `tests/Justina.Expense.UnitTests/ReceiptSubmissionServiceTests.cs` assert it is
stable for the same content and different for a different receipt. Both pass.

Manually: capture the `Idempotency-Key` from the stub log on a first attempt, force a failure, retry, and
confirm the header is byte-identical. Then submit a different receipt and confirm the key differs.

## What stays blocked

| Item | Blocked by |
|---|---|
| Any test that reaches the database | B1 |
| Real Expense API request/response shape, auth scheme, error contract | Plan risk R1 — no specification supplied |
| Recruitment API request/response shape | Plan risk R2 — phase 1 is routing only |
| Retry and circuit-breaker behaviour | No automated coverage; manual only (B2) |

Record results against the case ids in [test-cases.md](test-cases.md).
