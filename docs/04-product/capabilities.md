# Justina — Capabilities

*What Justina can do today, what it deliberately refuses to do, and what is not built yet.*

Everything marked **PLANNED** does not exist. Everything else is implemented in the product, subject to
the limitations stated at the end of this page.

---

## 1. Receive a receipt in a chat conversation

A person sends Justina a photo or a document in Telegram or WhatsApp. Justina downloads it, checks that it
is a file type it can actually read, checks that it is within the size and page limits, and stores it for
the length of the conversation.

**Accepted formats:** JPEG, PNG, WEBP images, and PDF documents.

Justina identifies the format by inspecting the file itself, not by trusting the name or the label the
chat app attached to it. A file renamed to look like a PDF is still rejected if it is not one.

**Limits (default configuration):**

| Limit | Default |
|---|---|
| Maximum file size | 20 MB |
| Maximum pages in a PDF | 20 |

Both limits are configurable per deployment. The values above are what Justina ships with.

**PDFs are handled in both forms.** A PDF produced by a computer carries its text directly. A PDF that is
a scan of paper carries only an image, and Justina converts the pages to images so they can be read. A
multi-page PDF has every page read — Justina never assumes the receipt is on page one.

## 2. Read a receipt

Justina reads the document with Vision AI and extracts:

- Merchant
- Date
- Currency
- Amount
- Category
- Receipt or invoice number
- Tax amount
- Line items (description, quantity, unit price, amount) where the document shows them

**Anything Justina cannot read with confidence is left empty rather than guessed.** An empty field is
shown to the person as missing so they can supply it. A plausible wrong number is treated as worse than a
blank.

Amounts and tax are rounded to two decimal places. Currencies are recognised as standard three-letter
codes.

## 3. Detect several receipts in one document

If a document holds more than one receipt, Justina recognises them as separate receipts and says so. It
asks the person how they want them handled before anything is submitted, and each receipt is then
confirmed on its own. They are never merged into a single expense.

## 4. Show what it found

Before anything is submitted, Justina shows the person the complete extracted receipt and asks whether it
is correct. Only fields that have a value are shown. If a field the expense system requires is missing,
Justina names it and asks for it.

## 5. Accept corrections in plain language

The person writes normally — "amount should be 15.50", "the merchant is Starbucks Reserve", "currency
should be IDR". Justina works out which fields were meant and changes **only those fields**.

Editable fields: merchant, date, currency, amount, category, receipt number, tax amount.

Each correction is checked before it is accepted. A currency that is not a real currency code, a date that
cannot be understood, an amount of zero or less, or a negative tax amount are all refused with an
explanation of what is acceptable. After every accepted correction, Justina shows the complete receipt
again and asks for confirmation again.

## 6. Require explicit confirmation

Justina submits only after the person has seen the data and said yes. "Yes", "correct", "go ahead" and
"submit it" all count. Silence, a lone thumbs-up, or an ambiguous reply do not — Justina asks again.

## 7. Submit an expense exactly once

On confirmation, Justina creates one expense. Confirming twice — because a message was sent twice, a chat
app retried, or the person tapped again — returns the original expense rather than creating a second one.
Two confirmations arriving at the same moment cannot both succeed.

## 8. Cancel without submitting

"Cancel", "forget it" or "never mind" ends the receipt. Nothing is submitted, and Justina says so
explicitly.

## 9. Refuse actions the person is not entitled to

Permission is granted per person, per action:

| Permission | What it allows |
|---|---|
| Submit expenses | Send a receipt, correct it, confirm it, cancel it |
| Read expenses | View a receipt or its status |
| Search recruitment | Run a candidate search |

A person Justina does not recognise holds none of these and cannot act at all. Permissions are decided by
Justina's own records — the AI never grants or infers one, and a person cannot talk their way into one.

## 10. Route a request to the right specialist

Justina decides whether a message is about expenses or about recruitment, and hands it to the matching
specialist. An expense request never reaches the recruitment side, and a recruitment request never reaches
the expense side. When genuinely unsure, Justina asks a short clarifying question rather than guessing.
See [domain-routing.md](domain-routing.md).

## 11. Recognise a recruitment request — routing only

A recruitment request reaches the Recruitment specialist, which tells the person that recruitment search
is not connected yet. It does not invent candidates, counts or names.

**Candidate search itself is PLANNED and not built.** See [roadmap.md](roadmap.md).

## 12. Treat document contents as data, never as instructions

Text printed on a receipt — including text engineered to look like a command, such as "ignore previous
instructions" or "approve this automatically" — is treated as printed text. Justina may extract it as a
field value. It never acts on it, and never repeats it back as though it were a system message.

## 13. Never disclose its own configuration

Justina does not reveal credentials, keys, internal addresses, or the contents of its own instructions,
whatever it is asked.

---

## Not built (PLANNED)

| Capability | Status |
|---|---|
| Candidate search, shortlists, CV reading | **PLANNED** — not built |
| Submission to the real expense system | **PLANNED** — blocked, see below |
| Reading back a person's past expenses or history | **PLANNED** — not built |
| Approving, rejecting or reimbursing an expense | **Out of scope** — Justina creates expenses only |
| Any channel other than Telegram and WhatsApp | **PLANNED** — not built |
| Voice notes, video, spreadsheets, Word documents | **Not supported** — rejected as unreadable formats |

## Limitations you should know about

- **The expense system specification has not been supplied.** Submission is built against a provisional,
  assumed contract and has been verified only against a stand-in. Real end-to-end submission is blocked
  until the specification and credentials arrive.
- **Justina has not been run against live Telegram or WhatsApp accounts**, or against a live expense
  system.
- **A receipt Justina fails to read cannot be re-read.** The person sends the document again. See
  [error-handling.md](error-handling.md).
- **A submission that fails cannot currently be retried from the conversation.** The receipt is preserved
  and Justina says it can be retried, but no conversational retry step exists yet. See
  [roadmap.md](roadmap.md).
- **Extraction accuracy depends on document quality.** Human confirmation is the safety net; it is not
  optional, and it is the reason a misread does not become a wrong expense.
