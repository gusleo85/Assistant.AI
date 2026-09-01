# Justina — Roadmap

*What exists today, what is blocked, and what is planned. Written so that nobody has to guess which is
which.*

---

## Today

Built, and working against a stand-in for the expense system:

| Capability | Notes |
|---|---|
| Telegram and WhatsApp conversations | Not yet run against live accounts |
| Receiving JPEG, PNG, WEBP and PDF receipts | 20 MB and 20 pages by default |
| Reading text PDFs, scanned PDFs and multi-page PDFs | Every page is read |
| Reading receipts with Vision AI | Unreadable fields left blank, never guessed |
| Detecting several receipts in one document | Asks first, never merges |
| Showing extracted data before anything is submitted | |
| Plain-language corrections, validated | Only the mentioned fields change |
| Re-showing and re-asking after every correction | |
| Explicit confirmation before submission | |
| Cancellation with nothing submitted | |
| Submitting exactly once, however many times a person confirms | |
| Permission checks per person, per action | Unrecognised users cannot act |
| Routing between Expense and Recruitment, and asking when unclear | |
| Recruitment requests answered honestly as not-yet-connected | Routing only |
| Treating document contents as data, never as instructions | |
| A full record of what happened to every receipt | |

---

## The two blockers

These are the reasons Justina cannot yet be used for real.

### Blocker 1 — The Expense API specification has not been supplied

Justina's submission step is built against a **provisional, assumed** contract. It has been verified
against a stand-in that behaves the way the assumption predicts. Nobody has confirmed the real expense
system behaves that way.

**Needed:** the expense system's specification, its address, how it authenticates, how it reports errors,
and sandbox credentials.

**Blocked until then:** submitting a real expense. Everything up to that point — reading, showing,
correcting, confirming, the permission checks, the once-only guarantee — is unaffected and does not change
when the specification arrives.

**Owner:** the human Product Owner.

### Blocker 2 — The Recruitment API specification has not been supplied

Recruitment is routing-only for exactly this reason. Rather than guess a contract, Justina reports
honestly that search is not connected.

**Needed:** the recruitment system's specification and credentials — or an explicit decision that
recruitment stays routing-only for now.

**Blocked until then:** all candidate search.

**Owner:** the human Product Owner.

---

## Also not yet done

Not blockers, but not done:

- **Justina has not been run against live Telegram or WhatsApp accounts.** Both channels are implemented;
  neither has been operated against a real account.
- **Justina has not been run against a live expense system.** Only against a stand-in.

Nothing in this documentation set should be read as a claim that Justina has been operated in production.

---

## Planned — not built

Everything in this section does not exist today.

### Near term

| Planned | Why it matters |
|---|---|
| **Real expense system integration** | Turns the whole journey from demonstrable into usable. Blocked on blocker 1. |
| **Retrying a failed submission from the conversation** | Today a failed submission is preserved and Justina says it can be retried, but the person has no way to trigger the retry. This gap should be closed before real use. |
| **Re-reading a document that failed to be read** | Today the person must send the document again. Acceptable, but a retry would be smoother. |
| **Live-channel verification** | Telegram first, then WhatsApp. |

### Later

| Planned | Notes |
|---|---|
| **Recruitment search execution** | Candidate search, results, shortlists. Blocked on blocker 2. |
| **Reading back past expenses** | "What did I submit last week?" — not built. |
| **Additional business areas** | The routing design supports adding one without changing the existing areas. |
| **Additional channels** | Nothing beyond Telegram and WhatsApp is planned in detail. |

### Explicitly out of scope

- Approving, rejecting or reimbursing an expense. Justina creates expenses; the approval process
  downstream is unchanged.
- Spending limits and policy checks. Justina does not judge whether an expense is allowed.
- Duplicate-receipt detection across conversations. Justina prevents one confirmation creating two
  expenses; it does not notice a person photographing the same receipt twice on different days.
- Voice notes, video, spreadsheets and Word documents.

---

## Decisions the Product Owner is being asked for

These are decisions, not recommendations. **Only the human Product Owner can make them.**

| # | Decision | What it unblocks |
|---|---|---|
| 1 | Supply the expense system's specification, address, authentication, error format and sandbox credentials | Real expense submission (blocker 1) |
| 2 | Supply the recruitment system's specification — or confirm recruitment stays routing-only for now | Candidate search (blocker 2) |
| 3 | Confirm which Telegram and WhatsApp accounts Justina should operate on | Live-channel verification |
| 4 | Confirm channel priority: Telegram first, WhatsApp second | Sequencing |
| 5 | Confirm where permissions come from: Justina's own records, or an existing identity system | How people are granted access |

---

## Where the code and the intended behaviour do not quite agree

Recorded honestly, so they are decided rather than discovered. None of these breaks a business rule.

1. **A failed submission cannot be retried from the conversation.** Justina tells the person the receipt is
   saved and can be retried, and internally it is held in a state a retry can act on — but no
   conversational step exists to trigger it. The promise and the available action do not match. *Fix
   before real use.*

2. **A failed reading cannot be retried.** Reading was separated from receiving specifically so a failed
   read could be retried without re-downloading, but a receipt that failed to be read cannot be re-read.
   In practice Justina asks for a clearer document, which is the better advice anyway — but the design
   intent and the behaviour differ.

3. **Missing fields are named with internal labels.** A person short an amount sees "missing Amount"; one
   short a date may see "missing ReceiptDate". Legible, but not how a person would name it. *Cosmetic
   wording fix.*

4. **Which receipt in a group is "the current one" is not firmly ordered.** When several receipts come
   from one document, they are all created at the same instant, and a confirmation given without naming a
   specific receipt resolves to the most recent — which is not reliably distinguishable within the group.
   The rule that they are confirmed one at a time and never merged still holds; which one a bare "yes"
   lands on may not. *Worth tightening before batches are used for real.*

5. **The task list is out of date.** `task_list.md` still describes the project as awaiting plan approval
   with no production code, while the expense journey is in fact built. The task list, not the product,
   is what is wrong here. *Should be brought up to date so it can be trusted.*

---

## Status

This document describes what exists and what does not. It is not an approval of anything.

**Accepting this delivery, approving the plan, and deciding what happens next are the human Product
Owner's alone.** The decisions listed above are what they are being asked to make.
