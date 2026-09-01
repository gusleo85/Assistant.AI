# Justina — Confirmation and Editing

*The safety net. This is the part of the product that makes it acceptable to let an AI read a receipt.*

---

## The principle

Justina proposes. The person decides.

Vision AI reading a photograph of a crumpled receipt will sometimes be wrong. The product's answer to that
is not "make the AI better" — it is: **nothing becomes an expense until a person has looked at the data
and said yes.** Every rule on this page follows from that.

---

## What the person is shown

After the document is read, and after **every** correction, Justina shows the complete receipt and asks
whether it is correct:

> I found:
>
> Merchant: Starbucks
> Date: 2026-08-31
> Currency: SGD
> Amount: SGD 12.50
> Category: Meals
> Receipt Number: INV-12345
> GST: SGD 1.03
>
> Is this correct?

**Rules for what is shown:**

- Only fields that have a value appear. An empty field is not shown as a blank line.
- Every value shown came from the document or from a correction the person made. Justina never fills a gap
  with something plausible.
- If a field required for submission is missing, Justina names it and asks for it, rather than presenting
  a receipt that cannot be submitted.

---

## Making a correction

The person writes normally:

| What they write | What changes |
|---|---|
| "amount should be 15.50" | Amount only |
| "the merchant is Starbucks Reserve" | Merchant only |
| "date should be August 30" | Date only |
| "currency should be IDR" | Currency only |
| "it was 15.50 at Starbucks Reserve" | Amount and merchant |
| "category is travel, not meals" | Category only |

**Only the fields the person mentioned change.** A field they did not mention is never adjusted, however
odd its value looks.

**Editable fields:** merchant, date, currency, amount, category, receipt number, tax amount.

The AI's job is to work out *which fields were meant*. Whether the resulting change is legal is decided by
Justina itself, not by the AI.

---

## What happens after every correction

1. The correction is checked.
2. If accepted, it is applied to those fields only.
3. **The complete receipt is shown again** — every field, not just the changed one.
4. **Confirmation is asked for again.**

A "yes" given before a correction never carries across it. If the person says "yes", then immediately says
"actually the amount was 15.50", Justina does not submit — the correction reopens the question, and the
person must confirm the corrected receipt.

Corrections may be made as many times as needed. There is no limit and no penalty.

---

## When a correction is refused

Justina checks every correction before accepting it, and says what would be acceptable:

| The person writes | Justina's answer |
|---|---|
| "currency should be XYZ" | *"'XYZ' is not a valid ISO-4217 currency code."* |
| "currency should be dollars" | *"Currency needs a three-letter ISO-4217 currency code, for example SGD."* |
| "amount should be zero" | *"Amount needs an amount greater than zero."* |
| "amount should be about twenty" | *"Amount needs an amount greater than zero."* |
| "tax should be -3.00" | *"Tax cannot be negative."* |
| "date should be sometime last week" | *"Date needs a date, for example 2026-08-31."* |
| "merchant should be" *(blank)* | *"Merchant needs a non-empty value."* |
| "change the total tax rate" | *"'total tax rate' is not an editable receipt field."* |

**A refused correction changes nothing.** The receipt stays exactly as it was, and the person is asked
again. Justina relays the reason and does not retry the same change hoping for a different answer.

---

## What counts as confirmation

**Counts as yes:** "yes", "correct", "that's right", "go ahead", "submit it", "confirm", and equivalents
in the language the person is writing in.

**Does not count:**

- Silence.
- A thumbs-up emoji on its own.
- "ok?" or anything else that reads as a question.
- "looks about right, I think".
- Any reply where the meaning is genuinely unclear.

When it is not clear, Justina asks. An extra question costs one turn; a wrongly submitted expense costs
more.

**Justina never confirms on the person's behalf** — however confident the extracted data looks, and
however obvious the answer seems.

---

## What counts as cancellation

"Cancel", "forget it", "never mind", "stop", and equivalents. Justina ends the receipt and states
explicitly that nothing was submitted. See [user-journeys.md](user-journeys.md) J9.

## What counts as "no"

If the person says no without saying what is wrong, Justina stops and asks what should change. It does not
submit, and it does not guess at what they meant.

---

## Confirming twice

A person may say yes twice — because the first reply seemed slow, or because the chat app resent the
message. The second confirmation returns the **same** expense, with the same reference. One expense
exists.

If two confirmations arrive at the very same instant, only one of them creates an expense. The other is
resolved against it.

This is guaranteed by Justina, not by the person being careful.

---

## After submission

Once submitted, the receipt is finished. It cannot be edited, and it cannot be cancelled — *"This receipt
has already been submitted and cannot be cancelled."* Changing or reversing a submitted expense is a
matter for the expense system.

---

## If submission fails

Justina says so honestly, and says the receipt is preserved. It does **not** ask for confirmation again —
the person already gave it, and asking twice for the same decision is both annoying and a route to
duplicate expenses.

**Current limitation:** the receipt is held in a state from which a retry is possible, but there is no way
to trigger that retry from the conversation today. See [roadmap.md](roadmap.md).

---

## Several receipts

When a document holds more than one receipt, each is shown, corrected and confirmed **on its own**. One
"yes" never covers more than one receipt, and Justina asks how the person wants the group handled before
anything is submitted. See [business-rules.md](business-rules.md) rule 10.
