# Receipt Testing

The receipt journey is the product. Everything else exists to serve it. This document walks the whole
thing end to end and shows how to verify each step in the database rather than trusting the chat window.

Covers `TC-RCP-01` .. `TC-RCP-12` from [test-cases.md](test-cases.md).

> These procedures need a running `justina-app` **and a SQL Server instance**. A former blocker (invariant
> globalization breaking `Microsoft.Data.SqlClient`) has been fixed and re-verified, but no database was
> ever available during the QA pass, so none of this has been executed. See
> [test-environment.md](test-environment.md).

## The lifecycle you are testing

```
Received ──▶ Extracting ──▶ WaitingConfirmation ──▶ Confirmed ──▶ Submitting ──▶ Submitted
                  │                 │  ▲                                │
                  │                 └──┘  edit                          ▼
                  ▼                                            SubmissionFailed ──▶ (retry)
           ExtractionFailed
                  
     Cancelled  ◀── from Received, Extracting, ExtractionFailed, WaitingConfirmation, Confirmed
```

Two things follow from that diagram and both are business rules:

- An edit returns the receipt to `WaitingConfirmation`. It never advances it. That is what forces a fresh
  confirmation after every change.
- `Cancelled` is not reachable from `Submitting` or `Submitted`. Once it is gone to the Expense API, it
  cannot be un-sent from here.

The aggregate is `src/Justina.Expense.Domain/Receipt.cs`. Illegal transitions throw
`ReceiptStateException`. You should never see that exception in a log: the command handlers check state
first and return `invalid_workflow_state` instead. A `ReceiptStateException` in the logs means a handler
is missing a guard, and is worth raising even if the user-visible behaviour looked fine.

## Reading the database

`State` is stored as an **integer**, not a string. You will be reading numbers.

| Value | State |
|---|---|
| 0 | Received |
| 1 | Extracting |
| 2 | ExtractionFailed |
| 3 | WaitingConfirmation |
| 4 | Confirmed |
| 5 | Submitting |
| 6 | Submitted |
| 7 | SubmissionFailed |
| 8 | Cancelled |

`Channel` is an integer too: `1` Telegram, `2` WhatsApp.

A shell you will use constantly:

```bash
docker compose exec justina-sqlserver /opt/mssql-tools18/bin/sqlcmd \
  -S localhost -U sa -P "$MSSQL_SA_PASSWORD" -C -d Justina -Q "
    SELECT TOP 5 Id, State, Merchant, ReceiptDate, Currency, Amount, TaxAmount,
                 ExternalExpenseId, BatchId, FailureReason
    FROM Receipts ORDER BY CreatedAtUtc DESC"
```

And the audit trail, which is the real evidence for most of these cases:

```bash
docker compose exec justina-sqlserver /opt/mssql-tools18/bin/sqlcmd \
  -S localhost -U sa -P "$MSSQL_SA_PASSWORD" -C -d Justina -Q "
    SELECT EventType, FromState, ToState, Actor, PayloadJson, CreatedAtUtc
    FROM ReceiptEvents WHERE ReceiptId = '<receipt id>' ORDER BY Id"
```

`ReceiptEvents` is append-only and written by the aggregate itself, so no state change can happen without
leaving a row. If the chat says something happened and there is no event row, the chat is wrong.

## Setting up a test user

There is **no seeding code**. A principal must be inserted by hand or nothing is authorized:

```sql
INSERT INTO Principals (Id, Channel, UserId, DisplayName, CapabilitiesJson)
VALUES (NEWID(), 1, '<your telegram user id>', 'QA Tester',
        '["expense.submit","expense.read"]');
```

To test refusals, create a second principal holding only `["expense.read"]`, and use a third user id with
no row at all.

---

## TC-RCP-01 — Extraction

**Steps**

1. Send a clear receipt photo to the bot.
2. The agent calls `justina.expense.receive_media` with the channel's media id.

**Expected response**

```json
{ "ok": true,
  "data": { "receiptCount": 1, "batchId": null,
            "receipts": [ { "receiptId": "...", "state": "WaitingConfirmation",
                            "merchant": "Starbucks", "date": "2026-08-31",
                            "currency": "SGD", "amount": 12.50,
                            "awaitingConfirmation": true, "isSubmittable": true,
                            "missingField": null, "externalExpenseId": null } ] } }
```

**Verify**

- `Receipts.State = 3`.
- `ReceiptEvents` holds `Created`, `ExtractionStarted`, `ExtractionCompleted` in that order.
- `Receipts.ExternalExpenseId IS NULL`.
- The WireMock request log is **empty**. Nothing may reach the Expense API before confirmation.

That last point is plan acceptance criterion 3, and it is the single most important check in this
document. Check it explicitly rather than assuming.

## TC-RCP-02 — Display

Compare the agent's chat message against the tool response field by field. The agent chooses wording; it
must not choose values. A number in the chat that is not in the snapshot is a fabrication and a failure.

The Expense Agent prompt shows only fields that have a value, so a receipt with no category simply omits
the category line. That is correct, not a bug.

## TC-RCP-03 — Validation

Send a receipt with an unreadable total. Extraction should return `amount: null` rather than a guess.

**Expected:** `isSubmittable: false`, `missingField: "Amount"`, and the agent asks for the amount instead
of offering to submit.

Confirming anyway returns:

```json
{ "ok": false, "error": { "code": "validation_failed",
    "message": "This receipt is missing Amount. Please provide it before confirming." } }
```

The check order is fixed — Merchant, ReceiptDate, Currency, Amount — and only the **first** missing field
is named. A receipt missing both merchant and amount reports `Merchant`. That is expected behaviour, not
an incomplete answer.

## TC-RCP-04 — Editing

The user writes freely. The agent turns that into a field patch and calls `justina.expense.edit_receipt`:

```json
{ "envelope": { ... }, "edits": [ { "field": "amount", "value": "15.50" } ] }
```

**Expected:** `amount` becomes `15.50`. Every other field is unchanged. Capture the full snapshot before
and after and diff them — this is the only way to catch a silent change to a field the user never
mentioned.

Field names the translator accepts:

| Field | Accepted names |
|---|---|
| Merchant | `merchant`, `vendor`, `store` |
| Date | `date`, `receiptdate`, `receipt_date` |
| Currency | `currency` |
| Amount | `amount`, `total` |
| Category | `category` |
| Receipt number | `receiptnumber`, `receipt_number`, `invoice`, `invoicenumber` |
| Tax | `tax`, `taxamount`, `tax_amount`, `gst`, `vat` |

Phrasings worth trying end to end, because the agent has to map each one:

- "the amount is wrong, it's 15.50"
- "merchant is Starbucks Reserve"
- "date should be August 30"
- "currency should be IDR"
- "GST was 1.40"
- "change the amount to 15.50 and the category to Travel" — two fields in one turn

The last one must produce a **single** `edit_receipt` call with two entries. Two separate calls also work
functionally, but they produce two `Edited` events and two re-displays, which is worth noting.

**Values the normalizer accepts.** These are proven by unit tests, so use them to check the agent is
passing the raw string through rather than pre-parsing it:

| Input | Parsed as |
|---|---|
| `12.50` | 12.50 |
| `SGD 12.50` | 12.50 |
| `$1,234.56` | 1234.56 |
| `1.234,56` | 1234.56 |
| `1,234` | 1234 |
| `12,50` | 12.50 |
| `not a number` | refused |

Dates: `2026-08-31`, `31/08/2026`, `31 August 2026`, `August 31, 2026`, `2026/08/31`.

## TC-RCP-05 — Re-display and re-confirmation

**Expected after every accepted edit:**

- The receipt is still in `WaitingConfirmation` (`State = 3`).
- A `ReceiptEvents` row with `EventType = 'Edited'`, `FromState = 3`, `ToState = 3`, `Actor` set to the
  user id, and `PayloadJson` listing the changed field names — for example `["Amount"]`.
- The agent re-displays the **complete** receipt, not just the changed line, and asks again.

An agent that says "updated" and then submits without re-asking is a failure even though the backend
would have accepted it.

## TC-RCP-06 — Invalid edits

Each of these is refused by `ReceiptEditTranslator` **before** the aggregate is touched, so the receipt is
completely unchanged afterwards. Verify that: re-read the snapshot and confirm nothing moved.

```json
{ "edits": [ { "field": "colour", "value": "blue" } ] }
→ validation_failed  'colour' is not an editable receipt field.

{ "edits": [ { "field": "amount", "value": "1" }, { "field": "total", "value": "2" } ] }
→ validation_failed  The field 'Amount' was supplied more than once.

{ "edits": [ { "field": "amount", "value": "0" } ] }
→ validation_failed  Amount needs an amount greater than zero.

{ "edits": [ { "field": "currency", "value": "Dollars" } ] }
→ validation_failed  Currency needs a three-letter ISO-4217 currency code, for example SGD.

{ "edits": [] }
→ validation_failed  No field changes were supplied.
```

Lower-case currency is **accepted** and upper-cased: `sgd` becomes `SGD`. Tax accepts `0`; amount does
not.

Editing a receipt that is not in `WaitingConfirmation` gives `invalid_workflow_state` —
"This receipt is `<State>` and can no longer be edited."

## TC-RCP-07 — Confirmation

The agent must call `confirm_receipt` only after the user has seen the data and explicitly agreed.

**Test the negatives too.** Reply to "Is this correct?" with each of these and confirm the agent asks
again rather than submitting:

- a thumbs-up emoji alone
- silence, then an unrelated message
- "I think so?"
- "the amount looks right" — a comment on one field, not consent

**Expected on a real "yes":** `Confirmed` event, then `SubmissionStarted`, then `Submitted`. Three rows,
in that order. `Receipts.ExternalExpenseId` is populated.

Confirming twice in a row from the chat is covered by `TC-RCP-09`.

## TC-RCP-08 — Cancellation

Say "cancel", "forget it" or "never mind".

**Expected**

- `Receipts.State = 8` (Cancelled).
- A `Cancelled` event with `Actor` set to the user id.
- `Conversations.ActiveWorkflow IS NULL` and `ActiveEntityId IS NULL` — the workflow is released, so the
  next message routes freshly.
- **The WireMock request log is still empty.** Check it. This is the whole point of the case.

Cancelling an already-cancelled receipt returns `ok: true` with the same snapshot — it is idempotent, not
an error. Cancelling a `Submitted` receipt returns `invalid_workflow_state`, "This receipt has already
been submitted and cannot be cancelled."

## TC-RCP-11 — Submission failure and retry

Point the stub at a `500` response and confirm.

**Expected**

- `SubmissionStarted` is written and persisted **before** the outbound call. Check the timestamps: the
  `Submitting` state must exist in the database even if you kill the container mid-flight. That is what
  makes a crash recoverable rather than ambiguous.
- On failure: `Receipts.State = 7` (SubmissionFailed), `FailureReason = 'external_api_failed'`.
- The user is told the receipt is saved and can be retried.
- On retry, confirmation is **not** requested again. The user already gave it.
- The retry carries the **same** `Idempotency-Key`, because the key is derived from receipt content, not
  from the attempt.

## Duplicate prevention

`TC-RCP-09` is the case, but it is worth understanding that three independent mechanisms should each stop
a duplicate on their own. Test them separately, because a single end-to-end pass can hide two broken
layers behind one working one.

**1. State guard.** `ConfirmReceiptCommandHandler` returns the existing snapshot immediately if the
receipt is already `Submitted`, without calling the API.
Test: confirm, wait for success, confirm again. Expect `ok: true`, the same `externalExpenseId`, and
still exactly one request in the stub log.

**2. Idempotency decorator.** `ConfirmReceiptCommand` declares the key `confirm:<receiptId>`. A replay
returns the stored result without running the handler at all.
Test: check `IdempotencyKeys` for a row with `KeyValue = 'confirm:<receiptId>'` and
`CommandType = 'ConfirmReceiptCommand'`. A second confirmation should log
`Replaying stored result for ConfirmReceiptCommand`.

**3. Database uniqueness.** `UX_Receipts_ExternalExpenseId` is a unique index filtered on
`WHERE [ExternalExpenseId] IS NOT NULL`. Two receipts cannot carry the same external id.
Test: try to write a duplicate directly and confirm SQL Server rejects it. The filter matters — without it
SQL Server would treat every unsubmitted receipt's `NULL` as a duplicate and block all of them.

**The header.** Independently of all three, the request carries
`Idempotency-Key: <sha256 hex>`, derived from receipt id, merchant, date, currency, amount, receipt number
and tax. Verify in the stub log that two attempts at the same receipt carry the **same** key, and two
different receipts carry **different** keys. Both are covered by unit tests
(`The_idempotency_key_is_stable_for_the_same_receipt_content`,
`Two_different_receipts_do_not_share_an_idempotency_key`).

**Concurrency (`TC-RCP-12`).** Fire two confirmations at the same instant:

```bash
for i in 1 2; do
  curl -s -X POST http://localhost:8080/tools/expense.confirm_receipt \
    -H "Content-Type: application/json" \
    -H "X-Justina-Tool-Key: $JUSTINA_TOOL_SECRET" \
    -d '{"envelope":{"channel":"telegram","userId":"u1","conversationId":"c1"},
         "receiptId":"<id>"}' &
done; wait
```

One wins. The other returns `conflict` — "Someone else changed this at the same time. Please check the
current state and try again." — from the `rowversion` column, or replays the first result. The stub must
show exactly one `POST /expenses` under every repetition. Run it ten times; a race that fails one time in
ten is still a failure.

## TC-RCP-10 — Several receipts in one document

Send a PDF containing three distinct receipts. Fixture recipe in
[pdf-testing.md](pdf-testing.md#multi-receipt-pdf).

**Expected from `receive_media`**

```json
{ "ok": true, "data": { "receiptCount": 3, "batchId": "<guid>",
                        "receipts": [ { ... }, { ... }, { ... } ] } }
```

**Verify**

- Three rows in `Receipts`, all sharing the same `BatchId`, all in state `3`.
- One row in `ReceiptBatches`.
- All three share the same `SourceMediaId`.
- The agent asks: "I found 3 receipts in this PDF. Would you like me to process them as 3 separate
  expenses?" — and **submits nothing** until answered.

**Then confirm them one at a time.** After the first is `Submitted`:

- `Conversations.ActiveWorkflow` is still `expense.receipt`. The workflow stays open while any member of
  the batch is non-terminal.
- After the last one is confirmed or cancelled, `ActiveWorkflow` becomes `NULL`.

**The failure to hunt for:** three receipts silently becoming one expense. Count the rows in the stub's
request log. Three confirmations must produce three `POST /expenses` with three different
`Idempotency-Key` values. One request, or two, is a failure of plan acceptance criterion 5 regardless of
what the chat said.
