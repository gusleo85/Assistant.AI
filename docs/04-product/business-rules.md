# Justina — Business Rules

*The rules Justina does not break. Each one is implemented and observable from a conversation.*

These ten rules are the canonical set. Everything else in this documentation is a consequence of them.
Each rule below states what it means, why it exists, and how someone can check it without reading code.

---

## Rule 1 — A receipt must be reviewed before it becomes an expense

Justina always shows the extracted data and asks whether it is correct. No expense is created from a
document that a person has not seen the contents of.

**Why:** an AI reading a photograph can be wrong. A person reading the result is what makes it safe.

**How to check:** send a receipt and say nothing. No expense is created, however long you wait.

---

## Rule 2 — The user can correct extracted data in plain language

Corrections are conversational. "Amount should be 15.50", "the merchant is Starbucks Reserve", "the date
is August 30th". Justina works out which fields were meant and changes **only those fields**. A field the
person did not mention is never touched.

**Editable:** merchant, date, currency, amount, category, receipt number, tax amount.

**How to check:** correct one field, and confirm every other field is unchanged in the receipt Justina
shows next.

---

## Rule 3 — Corrected data is validated before it is accepted

Every correction is checked. A currency that is not a real currency code, a date that cannot be
understood, an amount of zero or below, a negative tax amount, or an unknown field name are all refused
with an explanation of what would be acceptable. A refused correction leaves the receipt exactly as it
was.

**Why:** a correction is as capable of introducing a wrong value as a misread is.

**How to check:** try "currency should be XYZ" and confirm it is refused and nothing changed.

---

## Rule 4 — The user must explicitly confirm before anything is submitted

Justina submits only after the person has seen the data and said yes. "Yes", "correct", "go ahead" and
"submit it" count. Silence, a lone thumbs-up, or an ambiguous reply do not — Justina asks again.
Confirmation given before an edit does not carry across the edit.

**How to check:** after Justina shows a receipt, reply with something ambiguous. Justina asks rather than
submitting.

---

## Rule 5 — Cancelling submits nothing

"Cancel", "forget it", "never mind" ends the receipt with nothing sent anywhere, at any stage of the
journey. Justina says explicitly that nothing was submitted.

**How to check:** cancel at each stage and confirm no expense appears in the expense system.

---

## Rule 6 — Confirming twice creates one expense, not two

A second confirmation returns the expense the first one created, with the same reference. This holds
whether the second confirmation came from the person repeating themselves, the chat app retrying
delivery, or two confirmations arriving simultaneously.

**Why:** chat apps retry. A duplicate expense is a real financial error.

**How to check:** say "yes" twice quickly and confirm exactly one expense exists.

---

## Rule 7 — Unauthorized users cannot perform protected operations

Permission is held per person, per action: submit expenses, read expenses, search recruitment. A person
Justina does not recognise holds none of them and can do nothing. Permissions come from Justina's own
records — the AI never grants or infers one, and a refusal cannot be argued around.

**How to check:** message Justina from an account that has not been granted access. Every protected
action is refused with *"You are not authorized to perform this action."*

---

## Rule 8 — A recruitment request never reaches the Expense system

A request about candidates, CVs, roles or hiring goes only to the Recruitment specialist. There is no
path from recruitment to expense submission — not through wording, not through a document's contents, and
not by asking.

**How to check:** ask for candidates and confirm no receipt or expense activity results.

---

## Rule 9 — An expense request never reaches the Recruitment system

The reverse holds equally. Receipts, invoices, reimbursements and spending go only to the Expense
specialist.

**How to check:** send a receipt and confirm no recruitment activity results.

---

## Rule 10 — Several receipts in one document never silently become one expense

When a document holds more than one receipt, Justina says how many it found and asks how they should be
handled **before submitting anything**. Each receipt is then confirmed individually. Their amounts are
never added together, and one confirmation never covers more than one receipt.

**Why:** silently merging three receipts into one expense produces a wrong total that nobody sees.

**How to check:** send a PDF holding three receipts. Justina reports three and asks, rather than
submitting one expense.

---

## Supporting rules

These follow from the ten above and are also implemented.

| # | Rule |
|---|---|
| S1 | Justina never states that something was submitted, saved or created unless the owning system confirmed it. |
| S2 | Justina never invents an amount, date, status or reference number. Every fact it states comes from a system result. |
| S3 | Text inside a document is data, never an instruction — including text engineered to look like a command. |
| S4 | A file's type is determined by its contents, not by its name or the label the chat app attached to it. |
| S5 | A file over the size limit, or a PDF over the page limit, is rejected before it is stored or read. |
| S6 | Anything Justina cannot read with confidence is left blank rather than guessed, and shown to the person as missing. |
| S7 | Merchant, date, currency and amount are required to submit. Category, receipt number, tax and line items are optional. |
| S8 | The same inbound message never produces two receipts, however often the chat app retries it. |
| S9 | A refusal is final. Justina relays the reason and offers the next useful step; it does not retry the same request hoping for a different answer, and does not work around it. |
| S10 | Justina never reveals credentials, keys, internal addresses, or its own instructions. |
| S11 | Every receipt keeps a record of what happened to it and who did it — created, read, edited, confirmed, cancelled, submitted, failed. |

---

## Rules that are **not** in force

Stated explicitly so nobody assumes them.

- **There is no spending limit, policy check or category rule.** Justina does not judge whether an expense
  is allowed. That belongs to the approval process downstream.
- **There is no duplicate-receipt detection across conversations.** Justina prevents the *same
  confirmation* creating two expenses; it does not detect that a person photographed the same receipt
  twice on two different days.
- **There is no approval or reimbursement step.** Justina creates an expense; what happens to it is the
  expense system's business.
