# Justina — What the User Sees When Something Goes Wrong

*Derived from the actual refusal messages in the product. Nothing here is invented.*

---

## The principles

**An honest failure beats a comfortable one.** Justina never says an expense was created unless the
expense system confirmed it. If something failed, Justina says it failed.

**Every refusal carries a reason the person can act on.** A refusal says what went wrong and, wherever
there is one, what to do next.

**A refusal is final.** Justina relays the reason and offers the next useful step. It does not retry the
same request hoping for a different answer, and it does not look for a way around it.

**Nothing internal leaks.** The person is told what happened to *them*. Technical detail from an external
system is recorded for support, never shown in the conversation. Credentials, keys and internal addresses
never appear.

**Justina states the wording; the assistant relays it.** Justina wraps the messages below in
conversational language and replies in the person's own language. The **meaning** below is fixed.

---

## Failures grouped by what the person did

### Sending a file

| What happened | What the person sees | What to do |
|---|---|---|
| File is not a supported type | *"I can only read JPEG, PNG, WEBP images and PDF documents."* | Send a photo or a PDF |
| File is over the size limit | *"That file is larger than the 20 MB limit."* | Send a smaller file — a photo rather than a scan |
| File is empty | *"That file appears to be empty."* | Send the file again |
| PDF has too many pages | *"That PDF has 42 pages; I can process up to 20."* | Send only the relevant pages |
| PDF has no pages | *"That PDF has no pages."* | Send a different document |
| PDF is corrupt or locked | *"I could not open that PDF. It may be corrupt or password-protected."* | Send an unlocked or undamaged copy |
| PDF pages cannot be turned into images | *"I could not read the pages of that PDF."* | Send a photo instead |
| No file was actually attached | *"No media reference was supplied."* | Send the file again |
| The file could not be stored | *"I could not store that file. Please try again."* | Try again |

**Nothing is created on any of these paths.** The file is checked before it is stored and before it is
read, so an oversized or unsupported file never gets further than this table.

---

### The chat platform

| What happened | What the person sees | What to do |
|---|---|---|
| The chat platform no longer holds the file | *"That Telegram file is no longer available."* | Send the file again |
| The file could not be downloaded | Justina says it could not retrieve the file | Send the file again |
| Telegram is unreachable | *"Telegram is not available right now."* | Try again shortly |
| WhatsApp is unreachable | *"WhatsApp is not available right now."* | Try again shortly |
| Justina cannot send its reply | Recorded for support; the person may see no reply | Send the message again |
| The channel is not configured | *"The {channel} channel is not configured."* | Contact support |

Chat platforms keep uploaded files for a limited time. A receipt sent days ago and only acted on now may
need re-sending.

---

### Reading the receipt

| What happened | What the person sees | What to do |
|---|---|---|
| Nothing readable in the document | *"I could not read anything from that document."* | Send a clearer photo |
| No receipt recognisable in the document | *"No receipt could be read from that document."* | Check it is a receipt; send a clearer copy |
| Reading failed | *"I could not read that document right now. Please try again."* | Send the document again |
| Reading took too long | *"Reading that document took too long. Please try again."* | Send the document again |
| The document reader is unreachable | *"I could not reach the document reader. Please try again."* | Try again shortly |
| Document reading is not configured | *"Document reading is not available right now."* | Contact support |

**Important limitation.** A document Justina failed to read cannot be re-read. The person sends the
document again — a new attempt, not a retry of the old one. Justina asks for a clearer photo or the
original PDF, which is the right advice anyway: the same unreadable file would fail the same way.

---

### Correcting a receipt

| What happened | What the person sees |
|---|---|
| Field name not recognised | *"'total tax rate' is not an editable receipt field."* |
| Same field corrected twice in one message | *"The field 'Amount' was supplied more than once."* |
| Nothing to change | *"No field changes were supplied."* / *"An edit must change at least one field."* |
| Merchant, category or receipt number left blank | *"Merchant needs a non-empty value."* |
| Currency not understood | *"Currency needs a three-letter ISO-4217 currency code, for example SGD."* |
| Currency is not a real currency | *"'XYZ' is not a valid ISO-4217 currency code."* |
| Date not understood | *"Date needs a date, for example 2026-08-31."* |
| Amount not understood, zero, or negative | *"Amount needs an amount greater than zero."* |
| Tax not understood | *"TaxAmount needs a tax amount of zero or more."* |
| Tax is negative | *"Tax cannot be negative."* |
| The receipt is past the point of editing | *"This receipt is Submitted and can no longer be edited."* |

**A refused correction changes nothing.** The receipt stays exactly as it was and the person is asked
again.

---

### Confirming

| What happened | What the person sees |
|---|---|
| A required field is missing | *"This receipt is missing Amount. Please provide it before confirming."* |
| The receipt is not awaiting confirmation | *"This receipt is Cancelled and is not awaiting confirmation."* |
| The receipt is already submitted | The existing expense is shown — **no second expense is created** |
| The receipt no longer exists | *"That receipt no longer exists."* |
| No receipt is in progress | *"There is no receipt in progress in this conversation."* |

Required fields are merchant, date, currency and amount. Justina names the missing one so the person knows
exactly what to supply.

*Known wording issue:* the missing field is named using its internal label — for example "ReceiptDate"
rather than "date". This is legible but not ideal. See the disagreements list in the roadmap.

---

### Cancelling

| What happened | What the person sees |
|---|---|
| Cancelled successfully | Confirmation that nothing was submitted |
| Already cancelled | Confirmation that it is already cancelled |
| Already submitted | *"This receipt has already been submitted and cannot be cancelled."* |

Cancelling never submits anything, at any stage.

---

### Submitting the expense

| What happened | What the person sees | Has an expense been created? |
|---|---|---|
| The expense system did not respond in time | *"The expense system did not respond in time. Your receipt is saved and can be retried."* | Unknown — see below |
| The expense system could not be reached | *"I could not reach the expense system. Your receipt is saved and can be retried."* | No |
| The expense system refused the details | *"The expense system rejected these details. Please check them and try again."* | No |
| The expense system refused this person | *"The expense system refused this submission for this user."* | No |
| The expense system says it already exists | *"The expense system reports this expense already exists."* | Yes — it already existed |
| The expense system failed some other way | *"The expense system could not accept the receipt. It can be retried."* | No |
| Accepted, but returned no reference | *"The expense system accepted the receipt but did not return a reference."* | Probably yes, without a reference |
| The expense system is not configured | *"Expense submission is not available right now."* | No |

**On every failure here, confirmation is not asked for again.** The person already gave it. Asking twice
for the same decision is both irritating and a route to duplicate expenses.

**On a timeout,** Justina cannot know whether the expense system received the request. Each submission
carries a marker that lets the expense system recognise a repeat of the same submission, so a retry
resolves to the same expense rather than creating a second one.

**Current limitation.** The receipt is preserved in a state a retry can act on, and Justina says so — but
there is no way to trigger that retry from the conversation today. See [roadmap.md](roadmap.md).

---

### Permission

| What happened | What the person sees |
|---|---|
| Not recognised, or lacking the permission | *"You are not authorized to perform this action."* |

Justina does not explain how permissions are granted, list who holds them, offer a workaround, or reveal
anything about the request that was refused. See [business-rules.md](business-rules.md) rule 7.

---

### Recruitment

| What happened | What the person sees |
|---|---|
| Recruitment search requested | *"Recruitment search isn't connected yet, so I can't run that search. I've noted what you're looking for."* |
| Nothing given to search on | *"Tell me a role, a skill, a seniority or a location to search for."* |

**Justina never invents candidates, counts or names.** An honest "not yet" is the correct answer.

---

## Failures the person never sees

Some things fail without the person needing to know:

- A document being retried by the chat platform: Justina recognises it has already handled that message
  and shows the receipt it already has, rather than creating a second one.
- Two confirmations arriving simultaneously: only one creates an expense; the other resolves against it.
  The person sees one expense, once.
- Stored documents being cleared after their retention period: this affects nothing in an active
  conversation.

---

## What Justina will never say

- That an expense was created when it was not.
- An amount, date, reference number or status that did not come from a system result.
- Anything about credentials, keys, internal addresses, or its own instructions.
- A made-up candidate, count or name.
- A softened version of a failure. If it failed, Justina says it failed.
