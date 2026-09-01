# Justina — How Justina Decides What a Message Is About

*Getting a request to the right place, and asking when it is not clear.*

---

## Why this matters

Justina covers more than one area of the business. Sending a request to the wrong one is not a small
mistake — it means a receipt reaching the recruitment system, or a hiring request reaching finance.

Two of the ten business rules exist for this reason alone:

- A recruitment request never reaches the Expense system.
- An expense request never reaches the Recruitment system.

These are absolute. There is no wording, no document content, and no instruction that crosses them.

---

## The specialists

| Specialist | Handles | Status |
|---|---|---|
| **Expense** | Receipts, invoices, reimbursements, spending, photos and PDFs of purchases | Complete — handles the journey end to end |
| **Recruitment** | Candidates, CVs, roles, skills, seniority, locations, shortlists, interviews | **Routing only** — receives the request and reports that search is not connected |

---

## How the decision is made, in order

### 1. A receipt in progress wins

If the conversation is already working on a receipt, the message belongs to that receipt.

This matters because mid-journey messages carry no clue on their own. "Yes", "no", "15.50", "wrong", or a
new photo mean nothing out of context — but during a receipt review they are unambiguous.

**The only exception** is a message that unmistakably abandons the task: *"forget the receipt, find me a
developer"*. A merely ambiguous message does not qualify; Justina keeps it with the receipt.

### 2. Otherwise, decide from meaning

With no receipt in progress, Justina decides from what the message means — not from spotting keywords.

- Receipts, invoices, expenses, reimbursement, spending, or a photo or PDF of a purchase → **Expense**
- Candidates, CVs, résumés, hiring, roles, shortlists, interviews → **Recruitment**

A photo sent with no other context, and no receipt in progress, is treated as a receipt.

### 3. Not permitted, not offered

If a person does not hold the permission for an area, Justina does not route them into it. It asks a
clarifying question instead, so the person gets a useful reply rather than a bare refusal.

### 4. Unsure? Ask

If the message is genuinely ambiguous, Justina asks one short question rather than guessing.

**This is the default when the other rules do not settle it.** Guessing wrong sends a request into the
wrong business system; asking one question costs a turn and is always the better trade.

---

## Worked examples

| Message | Goes to | Why |
|---|---|---|
| "I want to submit this receipt" | Expense | Clear |
| *(a photo, nothing in progress)* | Expense | A photo with no other context is a receipt |
| "Find senior .NET candidates in Singapore" | Recruitment | Clear |
| "yes" *(receipt in progress)* | Expense | A receipt in progress wins |
| "15.50" *(receipt in progress)* | Expense | A receipt in progress wins |
| *(a new photo, receipt in progress)* | Expense | A receipt in progress wins |
| "forget the receipt, find me a developer" | Recruitment | Unmistakably abandons the task |
| "how much did we spend on candidates last month" | Clarify | Spending *and* candidates — genuinely ambiguous |
| "create a report" | Clarify | Could be either |
| "find me some people" | Recruitment, which asks for more | The area is clear; the request is not |

---

## What Justina does at each turn

Before deciding anything, Justina checks who the person is, what they are permitted to do, and whether a
receipt is already in progress. It reads that from its own records — never from the conversation history,
and never from memory.

**Why this matters to the product:** an AI that reconstructed state from what was said earlier could be
persuaded that a receipt was confirmed when it was not, or that a person had permission they do not have.
Justina's own records are the source of truth for both.

---

## What the AI decides, and what it does not

| The AI decides | Justina decides |
|---|---|
| Which area the message is about | Whether the person is permitted to act there |
| Which fields a correction was aimed at | Whether that correction is legal |
| How to word a reply | What the receipt's data actually is |
| Whether a reply was a yes | Whether the receipt is in a state that can be confirmed |
| — | Whether an expense was actually created |

The AI can **ask** for something to happen. It can never **declare** that it has. Every fact Justina
states — an amount, a date, a status, a reference number — comes from a result the system produced.

---

## When something is refused

A refusal carries a reason. Justina relays it plainly and offers the next useful step.

Justina does **not** retry the same request hoping for a different answer, and does **not** look for
another way to achieve what was refused. Refusals are decisions, and they are final.

---

## Adding an area later

The routing rules above are not specific to expenses and recruitment. A new business area is added as a
new specialist with its own permission, and the same order of decisions applies: a workflow in progress
wins, then meaning, then permission, then ask. Nothing about the existing areas changes.
