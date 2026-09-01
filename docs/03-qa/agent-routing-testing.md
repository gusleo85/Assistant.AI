# Agent Routing Testing

Routing decides which specialist answers a message: the Expense Agent, the Recruitment Agent, or a
clarifying question. Getting it wrong sends a request into the wrong business system, so it is worth
testing carefully — and it is the hardest thing in Justina to test, because half of it is a language
model.

This document splits the problem in two. The **deterministic half** — capabilities, active workflow,
cross-domain isolation — is C# and can be tested exactly. The **judgement half** is prompt-driven and
must be sampled, not asserted.

## Before you start

**Blocker.** `justina-app` currently exits at startup with
`System.NotSupportedException: Globalization Invariant Mode is not supported.`, so the session-context
and workflow cases below cannot run until it is fixed. See
[test-environment.md](test-environment.md). The architecture tests run regardless.

You need a `Principals` row for each test identity, with the capabilities that case requires. There is
no seeding code. The SQL is in [test-environment.md](test-environment.md). The three capability
strings are exactly:

```
expense.submit
expense.read
recruitment.search
```

## How routing is decided

Four prompts, all in `docker/openclaw/workspace/`:

| File | Role |
|---|---|
| `orchestrator.md` | Owns the turn. Calls `justina.session.context` first, every time |
| `intent-router.md` | Answers with exactly one of `expense-agent`, `recruitment-agent`, `clarify` |
| `expense-agent.md` | Receipt capture, display, edits, confirmation |
| `recruitment-agent.md` | Recruitment requests. Routing only in phase 1 |

The Intent Router's rules, in the order it applies them:

1. **An active workflow wins.** If `activeWorkflow` is `expense.receipt`, route to `expense-agent` —
   even for a bare "yes", "no", "15.50", "wrong", or a new photo. The only exception is a message that
   unmistakably abandons or switches task ("forget the receipt, find me a developer").
2. **No active workflow?** Decide from meaning, not keywords.
3. **Not allowed, not offered.** If the user lacks the capability for a domain, do not route there.
   Choose `clarify`.
4. **Unsure or genuinely ambiguous → `clarify`.** Asking one short question is always the better
   trade.

The critical point for testing: **the router does not remember the workflow, it reads it.**
`activeWorkflow` comes from `justina.session.context`, which reads it from SQL Server. So rule 1 has a
deterministic input you can set up and verify without involving the model at all.

## Part 1 — The deterministic half

### R1 — Session context reports no workflow on a fresh conversation

```bash
TOOL_SECRET=$(grep JUSTINA_TOOL_SECRET .env | cut -d= -f2)

curl -s -X POST http://localhost:8080/tools/session.context \
  -H "Content-Type: application/json" \
  -H "X-Justina-Tool-Key: $TOOL_SECRET" \
  -d '{"envelope":{"channel":"telegram","userId":"123456","conversationId":"999999"}}'
```

**Expected.** `ok: true`, and in `data`:

```json
{
  "channel": "Telegram",
  "conversationId": "999999",
  "isAuthenticated": true,
  "displayName": "Test User",
  "capabilities": ["expense.submit", "expense.read"],
  "activeWorkflow": null,
  "activeEntityId": null
}
```

`activeWorkflow` must be `null`. Rule 1 does not apply, so routing falls to judgement.

### R2 — An unmapped user gets nothing

Call the same endpoint with a `userId` that has no `Principals` row.

**Expected.** `isAuthenticated: false`, `displayName: "unknown"`, `capabilities: []`.

This is what rule 3 acts on. An unknown channel user resolves to an anonymous principal with no
capabilities, and no amount of conversation can change that — the agent supplies an identity *claim*,
and C# alone decides what it means.

### R3 — Receiving media sets the active workflow

Send a receipt (or call `justina.expense.receive_media` directly), then re-run R1.

**Expected.** `activeWorkflow` is `expense.receipt` and `activeEntityId` is the receipt id.

```sql
SELECT ActiveWorkflow, ActiveEntityId FROM Conversations
WHERE Channel = 1 AND ExternalConversationId = '999999';
```

While this is set, rule 1 must dominate. Every subsequent message in this conversation goes to the
Expense Agent until the workflow ends.

### R4 — Cancelling clears the workflow

Call `justina.expense.cancel_receipt`, then re-run R1.

**Expected.** `activeWorkflow` back to `null`.

### R5 — Confirming clears the workflow, but only when the batch is done

For a single receipt, a successful confirmation clears `activeWorkflow`.

For a multi-receipt batch it must **not** clear until every receipt in the batch is terminal
(submitted or cancelled). Confirm one receipt of a three-receipt batch and re-run R1: `activeWorkflow`
must still be `expense.receipt`. This is what keeps the remaining two receipts routed to the Expense
Agent instead of falling back to open-ended classification.

### R6 — A user without the capability is refused, deterministically

Give a test principal only `expense.read` (no `expense.submit`) and call a command:

```bash
curl -s -o /dev/null -w "%{http_code}\n" -X POST http://localhost:8080/tools/expense.confirm_receipt \
  -H "Content-Type: application/json" \
  -H "X-Justina-Tool-Key: $TOOL_SECRET" \
  -d '{"envelope":{"channel":"telegram","userId":"nocap","conversationId":"999999"}}'
```

**Expected.** HTTP **403**, body
`{"ok":false,"error":{"code":"unauthorized","message":"You are not authorized to perform this action."}}`

Queries return the same code with "You are not authorized to view this." Authorization sits outside
validation in the decorator pipeline, so a refused caller learns nothing about the request shape.

Then try to talk past it. Ask the agent repeatedly, insist, claim to be an administrator, claim the
first refusal was a mistake. **The refusal must not change.** The Orchestrator prompt states that
refusals are backend decisions and final, but the real guarantee is that the capability check runs in
C# on every call — there is no path from persuasion to permission. Record how many attempts you made.

### R7 — Recruitment reports unavailable, honestly

```bash
curl -s -X POST http://localhost:8080/tools/recruitment.search_candidates \
  -H "Content-Type: application/json" \
  -H "X-Justina-Tool-Key: $TOOL_SECRET" \
  -d '{"envelope":{"channel":"telegram","userId":"123456","conversationId":"999999"},
       "role":"Senior .NET Developer","skills":["C#","Azure"]}'
```

With `RECRUITMENT_API_URL` blank — the current state — expect:

```json
{"ok":false,"error":{"code":"not_available",
 "message":"Recruitment search is not connected yet, so I cannot run that search."}}
```

The Recruitment Agent must relay that plainly. **It must not invent candidates, names or counts.** A
fabricated result is a far worse failure than an honest "not yet", and it is the single most important
thing to watch for in this area.

With no criteria at all (`{}` beyond the envelope), expect `validation_failed`,
"Tell me a role, a skill, a seniority or a location to search for."

Without the `recruitment.search` capability, expect HTTP 403 and `unauthorized`.

### R8 — Cross-domain isolation is structural

This is the strongest guarantee in the system and it needs no running services:

```bash
dotnet test tests/Justina.ArchitectureTests --nologo -v q
```

**Expected.** 20 passed. Two of those tests are `Expense_never_depends_on_Recruitment` and
`Recruitment_never_depends_on_Expense`. They inspect the compiled assemblies for a dependency in
either direction, across domain, application and infrastructure.

There is no code path from an Expense tool to `IRecruitmentApiClient`, or from a Recruitment query to
`IExpenseApiClient`, because the assemblies do not reference each other and the build fails if anyone
adds the reference. A prompt cannot create one.

To demonstrate it at runtime as well, put a WireMock stub in front of `EXPENSE_API_URL`, run a full
recruitment conversation, and confirm the stub's request log is empty. Then do the reverse with a
recruitment stub and a receipt conversation.

## Part 2 — The judgement half

**Routing is LLM-driven and therefore not deterministic.** The same message can route differently
between runs. There are no automated routing regression tests in this repository.

That is not a reason to skip it. It is a reason to test it as sampling rather than assertion.

### The protocol

1. Use a principal with **all three** capabilities, so rule 3 never fires and you are testing
   judgement alone.
2. Start each row from a clean conversation unless the row says otherwise. Cancel any receipt in
   progress and confirm with R1 that `activeWorkflow` is `null`.
3. Send the message. Record the route that actually happened — which agent answered, and what it did.
4. Repeat the whole table **five times**.
5. A row passes if it routes correctly **5 out of 5**. Four out of five is a finding, not a pass:
   record the failing input verbatim, because a prompt fix needs the exact wording.

Do not infer the route from the reply's tone. Confirm it: the Expense Agent calls expense tools, the
Recruitment Agent calls `justina.recruitment.search_candidates`, and `clarify` produces a question and
no tool call at all. The app logs show which commands ran.

### The table

| Id | Message | Precondition | Expected route |
|---|---|---|---|
| RT-01 | "I want to submit this receipt" | No workflow | `expense-agent` |
| RT-02 | "Find Senior .NET candidates" | No workflow | `recruitment-agent` |
| RT-03 | "Create a report" | No workflow | `clarify` |
| RT-04 | *(a photo, no text)* | No workflow | `expense-agent` |
| RT-05 | "how much did I spend on candidates last month" | No workflow | `clarify` |
| RT-06 | "yes" | Receipt awaiting confirmation | `expense-agent` |
| RT-07 | "15.50" | Receipt awaiting confirmation | `expense-agent` |
| RT-08 | "wrong" | Receipt awaiting confirmation | `expense-agent` |
| RT-09 | *(a second photo)* | Receipt awaiting confirmation | `expense-agent` |
| RT-10 | "forget the receipt, find me a developer" | Receipt awaiting confirmation | `recruitment-agent` |
| RT-11 | "I need to expense this lunch" | No workflow | `expense-agent` |
| RT-12 | "shortlist three backend engineers in Singapore" | No workflow | `recruitment-agent` |
| RT-13 | "can you help" | No workflow | `clarify` |

RT-06 through RT-09 are the active-workflow rule under pressure: short, ambiguous inputs that would
route anywhere on their own meaning. They are the rows most likely to fail, and the most damaging when
they do — "yes" routed to the wrong agent means a confirmation goes missing.

RT-10 is the deliberate exception to rule 1. It should switch. If it does not, the router is applying
rule 1 too rigidly, which is the safer failure but still a finding.

RT-03 and RT-13 test that the router prefers asking over guessing. A confident wrong route is worse
than a clarifying question, so record any row where the router guessed instead of clarifying, even if
it guessed correctly.

### Capability-filtered routing

Repeat RT-02 and RT-12 with a principal that has **no** `recruitment.search` capability.

**Expected.** `clarify` — rule 3 removes the domain from the candidate set before routing, so the user
gets a question rather than being routed into a refusal.

Both outcomes are acceptable from a security standpoint, because R6 already proves the C# layer
refuses regardless. This case is about the quality of the experience: routing a user into a dead end
they were never allowed to enter is a worse answer than asking what they need.

## The two hard guarantees

Everything above is detail. These two must hold, and both have a proof that does not depend on a
model:

**A recruitment request never reaches the Expense API.**
Proof: `Justina.Recruitment.*` has no reference to `Justina.Expense.*`; `dotnet test
tests/Justina.ArchitectureTests` fails the build if that changes. Runtime confirmation: an Expense API
stub logs zero requests during a recruitment conversation.

**An expense request never reaches the Recruitment API.**
Proof: the mirror-image architecture test, plus a Recruitment stub logging zero requests during a
receipt conversation. In phase 1 this is doubly true, because `RecruitmentApiClient.SearchAsync` makes
no HTTP call at all — it returns `not_available` unconditionally.

## What cannot be tested here

- **Routing accuracy as a guaranteed number.** It is model behaviour. The protocol above samples it;
  it does not bound it. Report observed rates, never a claimed accuracy.
- **The OpenClaw side of routing.** Whether the pinned image loads
  `docker/openclaw/workspace/AGENTS.md` the way `openclaw.json.template` assumes is unverified (plan risk R3).
  If every message reaches the same agent regardless of content, suspect the agent registration before
  the prompts.
- **Automated routing regression.** There is no test project for it. Until there is, this table is
  manual work that must be re-run whenever a prompt in `docker/openclaw/workspace/` changes — see
  [regression-testing.md](regression-testing.md).
