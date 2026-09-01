# External API Integrations

Two business APIs, both reached only through a controlled client. The AI layer has no generic HTTP tool,
so "the model called the wrong API" is not a failure mode that exists here.

## Expense API

### Status: contract not supplied

**The real Expense API specification has not been provided** (plan risk **R1**). `ExpenseApiClient` posts a
documented **provisional** payload:

```json
{
  "merchant": "Starbucks",
  "date": "2026-08-31",
  "currency": "SGD",
  "amount": 12.50,
  "category": "Meals",
  "receiptNumber": "INV-12345",
  "taxAmount": 1.03,
  "submittedBy": "user-1",
  "lineItems": [ { "description": "Latte", "quantity": 1, "unitPrice": 12.50, "amount": 12.50 } ]
}
```

and reads the expense id from `id`, `expenseId` or `data.id`.

A useful amount of variation is **configuration, not code**:

```
ExpenseApi:BaseUrl              required
ExpenseApi:ApiKey               required
ExpenseApi:ApiKeyHeader         Authorization
ExpenseApi:ApiKeyPrefix         "Bearer "
ExpenseApi:SubmitPath           expenses
ExpenseApi:IdempotencyHeader    Idempotency-Key
ExpenseApi:CorrelationHeader    X-Correlation-Id
ExpenseApi:TimeoutSeconds       30
```

When the specification arrives, change `BuildPayload` and `ReadExpenseId` in
`src/Justina.Expense.Infrastructure/Api/ExpenseApiClient.cs`, plus the WireMock expectations in
`tests/Justina.IntegrationTests/ExpenseApiClientTests.cs`. Nothing else moves — that isolation is why
`ExpenseSubmission` is Justina's own contract rather than the API's.

With no `BaseUrl` configured the client logs an error and returns `not_available`, so an unconfigured
deployment says so instead of failing obscurely.

### Resilience

`AddStandardResilienceHandler()` supplies retry with exponential backoff and jitter, a circuit breaker and
a total-request timeout. On top of it the client sets a 30 s `HttpClient` timeout.

Retrying a submission is safe **because** the idempotency key travels with it: a retry the API already
processed resolves to the same expense.

### Error mapping

| Status | Code | Retryable |
|---|---|---|
| 401, 403 | `unauthorized` | no |
| 409 | `conflict` | no |
| 400, 422 | `validation_failed` | no — the data must change |
| 5xx, timeout, network | `external_api_failed` | yes |
| 2xx without an id | `external_api_failed` | yes |

Provider bodies are logged truncated to 500 characters and never relayed to the user. A failure after
confirmation leaves the receipt in `SUBMISSION_FAILED`, retryable through
`justina.expense.retry_submission` without asking the user to confirm again.

## Recruitment API

### Status: contract not supplied (plan risk R2)

`IRecruitmentApiClient` is defined so phase 2 is additive:

```csharp
bool IsConfigured { get; }
Task<Result<CandidateSearchResult>> SearchAsync(CandidateSearchCriteria, CancellationToken);
```

`RecruitmentApiClient` reports `IsConfigured == false` while `RecruitmentApi:BaseUrl` is empty, and the
query handler returns `not_available`. It does not guess a wire format — an honest "not connected" beats a
fabricated integration.

Phase 2 replaces the body of `SearchAsync`. The handler, the capability check and the routing already
work.

## OpenAI

Reached through `IVisionProvider`. See [vision-ai.md](vision-ai.md).

## Rules for any future client

1. Define Justina's own request/response types in Application; map to the wire format in Infrastructure.
2. Return `Result`, never provider exceptions.
3. Timeout, retry and circuit breaker via `AddStandardResilienceHandler`.
4. Propagate the correlation id.
5. Send an idempotency key for anything that creates something.
6. Log status codes and truncated bodies; never log credentials, never relay provider text to the user.
7. Fail closed and typed when unconfigured.
