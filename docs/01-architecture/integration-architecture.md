# Integration Architecture

Justina talks to five systems it does not own: two channels, two business APIs, and one AI provider.
Every one of them is reached through a narrow abstraction, so no SDK type ever appears in a domain rule.

## Channels

```text
IChannelMediaDownloader     resolve a channel media reference to bytes
IChannelResponder           send a text reply
IChannelRegistry            pick the adapter for a ChannelKind
```

Split deliberately: a caller that only needs to download does not depend on sending.

**Telegram** — two steps: `getFile` resolves a file path, then the bot file endpoint returns the bytes.
**WhatsApp Cloud API** — two steps: the media id resolves to a short-lived URL plus its MIME type, then
the bytes are fetched with the bearer token attached explicitly, because that URL is on another host.

Both produce the same `DownloadedMedia`. That is what lets one document pipeline serve both channels.

`ChannelRegistry` holds the only `switch` on channel in the system. An unconfigured channel returns a
typed "not configured" refusal instead of throwing.

## Ownership of transport

OpenClaw owns the channel connection, webhook verification and pairing; C# owns the normalized message
contract, media download and validation, deduplication and every business decision. This is Option C from
the plan, approved by the Product Owner.

The consequence worth knowing: webhook signature verification (`X-Hub-Signature-256` for WhatsApp, the
secret token for Telegram) is configured in the OpenClaw gateway, and the corresponding values are in
`.env`. `justina-app` exposes no public webhook endpoint — NGINX returns `404` for `/tools/`.

## Business APIs

```csharp
IExpenseApiClient       SubmitAsync(ExpenseSubmission, CancellationToken)
IRecruitmentApiClient   SearchAsync(CandidateSearchCriteria, CancellationToken)
```

`ExpenseSubmission` is **Justina's own contract**, not the external API's. Mapping to the wire format
happens in `ExpenseApiClient` alone, so a contract change touches one class and no test of the state
machine, validation or idempotency moves.

### Expense API — provisional contract

**The real Expense API specification has not been supplied** (plan risk R1). `ExpenseApiClient` posts a
documented provisional payload and reads the expense id from `id`, `expenseId` or `data.id`. The base
URL, key header, key prefix, submit path and the idempotency/correlation header names are all
configurable, so a good deal of contract variation is a settings change rather than a code change.

Integration tests run against a WireMock stub speaking that provisional contract. When the specification
arrives, the mapping and those expectations move together.

### Cross-cutting policy

Applied to the Expense client via `AddStandardResilienceHandler`:

| Concern | Behaviour |
|---|---|
| Authentication | Header injected from configuration; never logged |
| Timeout | 30 s by default |
| Retry | Transient failures only, exponential backoff with jitter |
| Circuit breaker | Included in the standard resilience pipeline |
| Idempotency | `Idempotency-Key` header on every submission |
| Correlation | `X-Correlation-Id` propagated from the inbound message |

Retrying a submission is safe precisely because the idempotency key travels with it: a retry the API
already processed resolves to the same expense rather than a second one.

### Error mapping

The user never sees a provider error body. HTTP status becomes a typed domain error:

| Status | Code | What the user is told |
|---|---|---|
| 401, 403 | `unauthorized` | The expense system refused this submission for this user |
| 409 | `conflict` | The expense system reports this expense already exists |
| 400, 422 | `validation_failed` | Details were rejected; check them and try again |
| 5xx, timeout, network | `external_api_failed` | Saved, and can be retried |
| 2xx without an id | `external_api_failed` | Accepted but no reference returned |

A failure after confirmation leaves the receipt in `SUBMISSION_FAILED`, which is retryable without asking
the user to confirm again — they already did.

### Recruitment API — phase 1

`RecruitmentApiClient` reports `IsConfigured == false` while no base URL is set and returns a typed
`not_available`. It does not guess a wire format. Phase 2 replaces the body of `SearchAsync`; nothing
above it changes.

## OpenAI Vision

Reached through `IVisionProvider` over the Responses API with a strict JSON schema. The API key is
configuration, attached as a bearer header by the DI-configured `HttpClient`, and never appears in a
prompt, a tool argument, a log line or a user-facing message. See
[vision-architecture.md](vision-architecture.md).

## What is deliberately not integrated

Nothing writes to the Expense or Recruitment systems except the two clients above. The AI layer has no
generic HTTP tool, no shell and no database access — so "the model called the wrong API" is not a failure
mode that exists here.
