# Justina — The Receipt Workflow

*The stages a receipt passes through, what the person sees at each one, and what can happen next.*

Justina — not the AI — owns where a receipt is in this workflow. The AI can ask for a receipt to move to
the next stage; it can never declare that it has. This is why a confident-sounding assistant cannot
produce an expense that was never confirmed.

---

## The stages

| Stage | What it means | What the person sees |
|---|---|---|
| **Received** | The document has arrived, passed the format, size and page checks, and been stored. | Nothing yet — Justina is working. |
| **Being read** | Vision AI is reading the document. | Nothing yet. |
| **Could not be read** | The document could not be read. | An explanation and a request to send it again. |
| **Awaiting confirmation** | The data has been extracted and shown. Justina is waiting for a yes, a correction, or a cancellation. | The complete receipt, and "Is this correct?" |
| **Confirmed** | The person said yes. Submission is about to begin. | Momentary — normally passed straight through. |
| **Submitting** | The expense system is being contacted. | Momentary. |
| **Submitted** | The expense exists in the expense system. | Confirmation, with the reference the expense system returned. |
| **Submission failed** | The expense system refused or could not be reached. The data is preserved. | An honest failure message. Confirmation is **not** re-asked. |
| **Cancelled** | The person ended it. Nothing was submitted. | Explicit confirmation that nothing was sent. |

**Submitted** and **Cancelled** are final. A receipt in either stage does not change again.

---

## How a receipt moves

```
        person sends a document
                  ↓
             Received
                  ↓
            Being read
              ↙        ↘
Could not be read      Awaiting confirmation ←──────┐
        ↓                    ↓        ↓             │
    Cancelled          Cancelled   correction ──────┘
                             ↓   (shown again, asked again)
                         Confirmed
                             ↓
                        Submitting
                          ↙      ↘
                  Submitted    Submission failed
```

**Reading the diagram:**

- A correction returns the receipt to *Awaiting confirmation*. This is the mechanism that forces Justina
  to re-show the receipt and re-ask for confirmation after every single edit.
- *Awaiting confirmation* is the only stage from which a receipt can be confirmed.
- Cancellation is available at every stage up to and including *Confirmed* — a person can still stop
  things after saying yes, provided submission has not completed.
- There is exactly one route to *Submitting*, and it starts at *Confirmed*. There is no path from
  *Awaiting confirmation* straight to submission.

---

## What can and cannot happen at each stage

| If the person… | …while the receipt is | Justina |
|---|---|---|
| Corrects a field | Awaiting confirmation | Applies it, shows the receipt again, asks again |
| Corrects a field | Any other stage | Refuses: *"This receipt is {stage} and can no longer be edited."* |
| Says yes | Awaiting confirmation, all required fields present | Submits |
| Says yes | Awaiting confirmation, a required field missing | Refuses and names the missing field |
| Says yes | Already submitted | Shows the existing expense — no second expense |
| Says yes | Any other stage | Refuses: *"This receipt is {stage} and is not awaiting confirmation."* |
| Cancels | Received, being read, could not be read, awaiting confirmation, confirmed | Cancels, submits nothing |
| Cancels | Already cancelled | Confirms it is already cancelled |
| Cancels | Already submitted | Refuses: *"This receipt has already been submitted and cannot be cancelled."* |

---

## Required and optional data

**Required before a receipt can be submitted:**

- Merchant
- Date
- Currency (a valid three-letter code)
- Amount (greater than zero)

**Optional:** category, receipt or invoice number, tax amount, line items.

If a required field is missing, Justina names it and asks for it. A confirmation given while a required
field is missing does not submit — Justina asks for the field instead.

---

## Several receipts in one document

When a document holds more than one receipt, each becomes its own receipt on this workflow, and they are
grouped together so Justina knows they came from the same document.

- Every one of them starts at *Awaiting confirmation*.
- Each is shown, corrected and confirmed **individually**.
- The conversation's receipt workflow stays open until every receipt in the group has reached *Submitted*
  or *Cancelled*.
- Nothing is submitted until the person has been told how many receipts were found and has said how they
  want them handled.

---

## What is recorded

Every movement through this workflow is recorded against the receipt: what happened, which stage it moved
from and to, who caused it, and when. Edits record which fields were changed.

This means the question *"how did this expense come to exist?"* has an answer — including who confirmed
it and what the data looked like when they did.

---

## What Justina keeps, and for how long

The document itself is kept only while it is needed — a few hours by default — and is then removed
automatically. The receipt's data and its history are kept.

Justina is not the record of the expense. Once submitted, the expense system holds it.

---

## One conversation, one receipt at a time

A conversation works on one receipt (or one group of receipts from a single document) at a time. While
that is in progress, short replies — "yes", "no", "15.50", "wrong" — belong to it. See
[domain-routing.md](domain-routing.md).

When the receipt reaches *Submitted* or *Cancelled*, the workflow closes and the next message starts
fresh.
