# Channels

Justina supports **Telegram** and **WhatsApp**. Both reduce to the same normalized envelope before any
business logic runs, so no domain code contains a channel-specific structure.

## The abstractions

`src/Justina.Core.Application/Channels/ChannelAbstractions.cs`:

```csharp
IChannelMediaDownloader    ChannelKind Channel; DownloadAsync(MediaReference) → DownloadedMedia
IChannelResponder          ChannelKind Channel; SendTextAsync(conversationId, text)
IChannelRegistry           GetDownloader(ChannelKind); GetResponder(ChannelKind)
```

Two interfaces rather than one `IChannel`: a caller that only downloads should not depend on sending.

`ChannelRegistry` holds the only `switch` on channel in the system. An unconfigured channel returns a
typed `not_available` refusal rather than throwing.

## The normalized envelope

```csharp
record InboundMessage(
    ChannelKind Channel, string UserId, string ConversationId, string MessageId,
    InboundMessageKind Kind, string? Text, MediaReference? Media, DateTimeOffset ReceivedAtUtc);

record MediaReference(string MediaId, string MimeType, string? FileName, long SizeBytes);
```

`MediaId` is the channel's own identifier. Justina stores the reference, not the channel's payload shape,
and resolves it to bytes through the downloader when it needs them.

## Telegram

`Channels/Telegram/TelegramAdapter.cs`. Two-step media fetch:

```text
GET  bot{token}/getFile?file_id={mediaId}   →  result.file_path
GET  file/bot{token}/{file_path}            →  bytes
```

Replies: `POST bot{token}/sendMessage` with `chat_id` and `text`.

The bot token appears in the URL path, so it is never logged — log lines carry status codes only.

Configuration: `Telegram:BotToken`, `Telegram:ApiBaseUrl`, `Telegram:TimeoutSeconds`.

## WhatsApp (Cloud API)

`Channels/WhatsApp/WhatsAppAdapter.cs`. Also two steps, but the second is on another host:

```text
GET  {mediaId}          →  { url, mime_type }
GET  {url}              →  bytes     (bearer token attached explicitly)
```

The explicit `Authorization` header on the second request matters: the media URL is on a different host,
so the `HttpClient` default header does not apply and the download would 401 without it.

Replies: `POST {phoneNumberId}/messages` with the standard `messaging_product: whatsapp` text payload.

Configuration: `WhatsApp:AccessToken`, `WhatsApp:PhoneNumberId`, `WhatsApp:GraphBaseUrl`,
`WhatsApp:AppSecret`, `WhatsApp:WebhookVerifyToken`, `WhatsApp:TimeoutSeconds`.

## Who terminates the webhook

**OpenClaw owns transport** — the channel connection, webhook verification (`X-Hub-Signature-256` for
WhatsApp, the secret token for Telegram) and pairing. This is Option C from the plan, approved by the
Product Owner.

**C# owns everything else** — the normalized contract, media download and validation, deduplication, and
every business decision.

Consequence: `justina-app` exposes no public webhook endpoint, and NGINX returns `404` for `/tools/`. The
WhatsApp app secret and verify token still live in `.env` because the gateway needs them.

## Deduplication

`SqlServerInboundMessageDeduplicator` registers `(Channel, MessageId)` with a composite primary key. A
replayed webhook loses the insert race and is dropped; the tool returns the existing outcome instead of
reprocessing the document.

## Adding a channel

1. Add a value to `ChannelKind`.
2. Implement `IChannelMediaDownloader` and `IChannelResponder`.
3. Register both plus a configured `HttpClient` in `CoreInfrastructureServiceCollectionExtensions`.
4. Add the channel name to `RequestContextFactory.TryParseChannel` and to the envelope enum in
   `justina-tools.json`.
5. Configure the channel in `openclaw.json.template` and add its variables to `.env.example`.

No domain or application code changes. That is the whole point of the abstraction.

## Testing

Both adapters are plain `HttpClient` consumers, so they test against WireMock the same way the Expense
client does. The channel-independent parts — envelope handling, deduplication, document processing — are
already covered without a channel at all.
