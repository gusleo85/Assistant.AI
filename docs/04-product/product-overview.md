# Justina — Product Overview

*Audience: anyone who needs to understand what Justina does without reading code.*

---

## What Justina is

Justina is a business assistant that people talk to in a chat app they already use. There is no new
application to install, no portal to log into, and no form to fill in. A person sends Justina a message —
or a photo of a receipt — and Justina does the work in the conversation.

Today Justina is reachable on **Telegram** and **WhatsApp**.

## What Justina does today

**Turning a receipt into an expense.** This is the one business task Justina can complete from beginning
to end. Someone photographs a receipt, or forwards a PDF invoice, and Justina reads it, shows what it
found, accepts corrections in plain language, asks for confirmation, and submits the expense once the
person has said yes.

**Recognising a recruitment request and answering honestly.** Justina understands when someone is asking
about candidates or hiring rather than expenses, and routes the request to the right specialist. That
specialist currently reports that candidate search is not connected yet. It does not invent candidates.
See [roadmap.md](roadmap.md).

## The idea behind it

Three principles shape everything Justina does.

**Nothing happens without a person saying yes.** Justina reads documents and proposes data. It never
decides that data is correct. A person always sees the extracted values and explicitly confirms before an
expense is created. This is what makes it safe to let an AI read a receipt.

**Justina never claims something happened unless it did.** Every fact Justina states — an amount, a date,
a reference number, a status — comes from the system that owns it. If a submission failed, Justina says it
failed. It does not soften, guess, or fill a gap with something plausible.

**People write the way they normally write.** Corrections are conversational: "amount should be 15.50",
"merchant is Starbucks Reserve", "the date is August 30th". Justina works out which fields were meant, and
changes only those.

## Who it is for

Employees who need to claim business expenses and would rather send a photo than fill in a form, and the
finance function that receives those expenses.

Access is granted per person. Someone Justina does not recognise cannot submit an expense, cannot read a
receipt, and cannot run a recruitment search. See [business-rules.md](business-rules.md).

## What Justina is not

- **Not an approver.** Justina creates an expense; it does not decide whether it is allowed or reimburse
  anyone. Whatever approval process exists downstream is unchanged.
- **Not an accounting system.** Justina holds a receipt only long enough to get it through the
  conversation. The expense system remains the record.
- **Not a recruiter.** See [roadmap.md](roadmap.md).
- **Not autonomous.** Justina does not act on a schedule, chase anyone, or take action without a message
  from a person.

## Current state, stated plainly

The expense journey is built and works against a stubbed expense system. Two things are still open:

1. **The specification for the real expense system has not been supplied.** Justina's submission step is
   built against a provisional, assumed contract. Until the real specification and credentials arrive,
   expenses cannot reach the live expense system.
2. **Justina has not yet been run against live Telegram or WhatsApp accounts**, or against a live expense
   system.

Both are tracked in [roadmap.md](roadmap.md). Nothing in this documentation set should be read as a claim
that Justina has been operated in production.

## Where to read next

| Question | Document |
|---|---|
| What exactly can Justina do? | [capabilities.md](capabilities.md) |
| What happens, step by step? | [user-journeys.md](user-journeys.md) |
| What rules does it never break? | [business-rules.md](business-rules.md) |
| What are the stages of a receipt? | [receipt-workflow.md](receipt-workflow.md) |
| How do corrections and confirmation work? | [confirmation-and-editing.md](confirmation-and-editing.md) |
| Which chat apps, and what can I send? | [supported-channels.md](supported-channels.md) |
| How does Justina know which request is which? | [domain-routing.md](domain-routing.md) |
| What does the user see when something fails? | [error-handling.md](error-handling.md) |
| What is built, and what is planned? | [roadmap.md](roadmap.md) |
