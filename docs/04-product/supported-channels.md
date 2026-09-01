# Justina — Supported Channels

*Where people can reach Justina, and what they can send.*

---

## The channels

| Channel | Status |
|---|---|
| **Telegram** | Built |
| **WhatsApp** | Built |
| Anything else (Slack, Teams, email, SMS, web chat, voice) | **PLANNED / not built** |

A message arriving from any channel other than Telegram or WhatsApp is not accepted.

**Not yet verified against live accounts.** Both channels are implemented, but Justina has not been run
against live Telegram or WhatsApp accounts. See [roadmap.md](roadmap.md).

---

## The experience is the same on both

Justina behaves identically on Telegram and WhatsApp. The same rules, the same journey, the same wording,
the same limits, the same refusals. Nothing in [business-rules.md](business-rules.md) varies by channel.

This is deliberate: a person moving between the two should not have to learn anything new, and support
should never have to ask "which app were you using?" to explain Justina's behaviour.

---

## What a person can send

| They send | Justina |
|---|---|
| A photo of a receipt | Reads it |
| A PDF invoice | Reads it |
| A scan of a receipt as a PDF | Reads it |
| A multi-page PDF | Reads every page |
| A text message | Understands it and routes it |
| A Word document, spreadsheet, video, voice note, sticker | Refuses, saying what it can read |

**Accepted file types:** JPEG, PNG, WEBP images, and PDF documents.

**Limits (default):** 20 MB per file, 20 pages per PDF. Both are configurable per deployment.

Justina identifies a file's type from its contents, not from its name or the label the chat app attached
to it. A file renamed to look like a PDF is still refused if it is not one.

---

## Identity and permission

A person is identified by the account they message from, and their permissions are attached to that
identity in Justina's own records.

- Telegram and WhatsApp identities are separate. Being known on one does not make a person known on the
  other; each identity is granted permissions independently.
- Someone Justina does not recognise holds no permissions and cannot act.
- Permissions are never inferred from what a person says, and cannot be argued into existence.

See [business-rules.md](business-rules.md) rule 7.

---

## Conversations

Each conversation carries its own state. A receipt being reviewed in one conversation is not visible in
another, and Justina will not act on a receipt belonging to a different conversation.

While a receipt is being reviewed, short replies — "yes", "no", "15.50" — belong to that receipt. See
[domain-routing.md](domain-routing.md).

---

## Language

Justina replies in the language the person wrote in.

---

## Duplicate and retried messages

Chat apps retry deliveries. Justina recognises a message it has already handled and shows the receipt it
already has, rather than creating a second one. The person sees one receipt.

---

## When a channel is unavailable

If a chat platform cannot be reached — to download a file the person sent, or to send a reply — Justina
says so plainly rather than failing silently. See [error-handling.md](error-handling.md).

A file that a chat platform no longer holds cannot be retrieved: Justina asks the person to send it again.
Chat platforms keep uploaded files for a limited time, so a receipt sent days earlier and only acted on
later may need re-sending.

---

## What is not available on any channel

- Voice notes are not read.
- Justina does not start conversations. It only replies to a message from a person.
- Justina does not send reminders, chase anyone, or act on a schedule.
- There is no group-chat behaviour defined. Justina is designed for one person in one conversation.
