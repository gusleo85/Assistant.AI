# C# Architecture

## Clean Architecture, per domain

```text
Justina.Api (presentation)
        ▼
*.Application (use cases, CQRS handlers, validation)
        ▼
*.Domain (entities, value objects, state machine, invariants)
        ▲
*.Infrastructure (EF Core, HTTP clients, OpenAI, channels) — implements Application interfaces
```

Business logic never references the OpenAI SDK, a channel SDK, `HttpClient`, `DbContext` or
`IConfiguration`. Configuration reaches the domain as typed options resolved at the composition root.

## SOLID in practice

**Single responsibility.** There is no `JustinaService`. The seams are `IVisionProvider`,
`IDocumentProcessor`, `IReceiptRepository`, `IExpenseApiClient`, `IConversationStateStore`,
`IIdempotencyStore`, `IAuthorizationService`, `IChannelMediaDownloader`, `IChannelResponder`.

**Open/closed.** A new domain is a new project triple plus tool registration. Core is untouched — see
[Adding a new domain](#adding-a-new-domain).

**Liskov.** Every `IVisionProvider` returns the same normalized result and fails the same way, through
`Result`. Provider-specific exceptions never escape Infrastructure.

**Interface segregation.** Channels are two interfaces, not one fat `IChannel`, so a downloader consumer
does not depend on sending.

**Dependency inversion.** Application defines the interfaces; Infrastructure implements them; `Api` binds
them.

**What we did not do.** No generic `IRepository<T>`, no repository per entity, no abstraction with one
implementation and no plausible second. `IReceiptRepository` exists because the Expense aggregate needs
one; `IVisionProvider` exists because it is a genuine seam for both testing and a future provider.

## Result over exceptions

```csharp
public sealed record Error(string Code, string Message);
Result<T>  // IsSuccess / IsFailure / Value / Error
```

Expected refusals — unauthorized, wrong state, unreadable document, API timeout — are values, because the
agent must relay them to a person. Exceptions are reserved for defects: `DomainException` and
`ReceiptStateException` mean a caller asked for something the lifecycle forbids, which is a bug in the
caller, not a user outcome.

Error codes in `ErrorCodes` are a stable contract. They reach the AI layer, so they never carry secrets
or internal detail.

## CQRS — where, and where not

Applied to the **Expense receipt workflow only**, because that is where the asymmetry is real: commands
mutate an audited state machine and need validation, authorization and idempotency; queries render for
display and must not mutate.

```text
Commands  ReceiveReceipt, ExtractReceipt, UpdateReceipt, ConfirmReceipt, CancelReceipt, SubmitExpense
Queries   GetReceipt, GetReceiptStatus, GetSessionContext
```

Not applied to Recruitment reads, health endpoints, or Core services — those are plain services. CQRS
everywhere would be ceremony.

**No mediator library.** MediatR is commercially licensed now, and the pipeline we need is a handful of
decorators. `HandlerRegistration.AddCommandHandler` wires them explicitly, so reading one method tells you
the exact path a command takes:

```text
Logging → Authorization → Validation → Idempotency → handler
```

Authorization sits outside validation deliberately: a caller who may not do something should not learn
the shape of the request they were refused.

## The state machine

`Receipt` is the aggregate root and the only thing that may change receipt state.

```text
RECEIVED → EXTRACTING → WAITING_CONFIRMATION → CONFIRMED → SUBMITTING → SUBMITTED
                │              │  ▲                            │
                ▼              ▼  └── edit ───────────────────┘
        EXTRACTION_FAILED  CANCELLED                    SUBMISSION_FAILED → retry
```

Every transition is a method, guards its precondition, writes a `ReceiptEvent`, and throws
`ReceiptStateException` if the transition is illegal. Handlers check state first and return a typed
refusal, so the exception really does mean "defect".

An edit returns the receipt to `WAITING_CONFIRMATION`, which is what structurally forces the agent to
re-display and re-ask.

## Persistence notes

- Domains contribute EF mappings through `IModelConfiguration`, so `JustinaDbContext` never references a
  domain project. `Justina.Expense.Infrastructure` registers `ExpenseModelConfiguration`.
- `DateTimeOffset` is stored as `datetime2` in UTC via a converter applied once in `ConfigureConventions`.
- Money is `decimal(18,2)`, declared explicitly — the EF default is not what this domain needs.
- `EfUnitOfWork` translates `DbUpdateConcurrencyException` and SQL Server error 2601/2627 into a typed
  `conflict` result, so a race becomes a message rather than a 500.

## Adding a new domain

1. Create `Justina.<Domain>.Domain`, `.Application`, `.Infrastructure`.
2. `.Domain` references only `Justina.Core.Domain`; `.Application` adds `Justina.Core.Application`.
3. Define use cases as commands and queries implementing `ICommand<T>` / `IQuery<T>`; add
   `IRequireCapability` where an action is protected, and `IIdempotentCommand` where a retry must not
   repeat an effect.
4. Add capabilities to `Capabilities` in `Justina.Core.Domain`.
5. If the domain persists anything, add an `IModelConfiguration` in its Infrastructure project and
   register it; then `dotnet ef migrations add <Name>`.
6. Register handlers in a `Add<Domain>Application()` extension and bind implementations in
   `Add<Domain>Infrastructure()`.
7. Call both from `Program.cs`.
8. Add tool endpoints in `ToolEndpoints`, declare them in `docker/openclaw/tools/justina-tools.json`, and
   write an agent prompt in `docker/openclaw/agents/`.
9. Teach the Intent Router the new domain.
10. Add an architecture test asserting the new domain does not reference the existing ones.

Nothing in Core changes.
