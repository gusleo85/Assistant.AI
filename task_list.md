# Justina — Master Task List

Companion to `plan/task.md`. One line per deliverable, ticked only when the work exists in the repository
**and** has been verified.

**Legend:** `[x]` done and verified · `[~]` implemented but not verified end to end · `[ ]` not started ·
`[!]` blocked

**Current state:** plan approved; first implementation pass complete. Solution builds; **143 automated
tests pass**. The full Docker stack, live channels and a live Expense API have **not** been exercised —
those are `[~]`, not `[x]`.

---

## Phase 0 — Planning (PLANNER)

### 0.1 Repository inspection (§42, §61)

- [x] Existing repositories / source — empty, no commits on `main`
- [x] Existing C# architecture — none
- [x] Existing Docker configuration — none
- [x] OpenClaw configuration — none in repo
- [x] Available agents / tools — none
- [x] Existing APIs and contracts — none (Expense + Recruitment specs missing)
- [x] Authentication and authorization — none
- [x] Existing tests — none
- [x] Existing documentation — `docs/` existed but was empty
- [x] Local toolchain — .NET SDK 10.0.400, Docker 29.3.1, Compose v5.1.1

### 0.2 External capability verification (§23)

- [x] OpenClaw architecture (Node.js gateway, channel plugins, `openclaw.json`, WS control plane)
- [x] OpenAI direct PDF input support and limits (100 pages / 32 MB)
- [ ] **Spike:** how custom tools register with OpenClaw (MCP vs skill vs plugin) — risk R3
- [ ] **Spike:** exact OpenClaw container image/tag, volumes, config mount
- [ ] **Spike:** OpenAI structured-output schema and direct-PDF call shape against the live API

### 0.3 Plan document (§43)

- [x] `plan/task.md` with all 32 sections
- [x] AI ↔ C# boundary recorded
- [x] CQRS decision recorded (Expense workflow only, hand-rolled, no MediatR)
- [x] Docker and network design recorded
- [x] Security and idempotency design recorded
- [x] Risks and alternatives recorded
- [x] "Decisions Required From The Product Owner" section
- [x] Database changed from PostgreSQL to SQL Server; risk R11 added
- [x] `task_list.md` created

### 0.4 Approval gate (§44)

- [x] **HUMAN APPROVAL of `plan/task.md`** — "Plan approved, proceed with coding"
- [!] Expense API contract, base URL, auth scheme, error format, credentials — **blocked on PO (R1)**
- [!] Recruitment API contract — **blocked on PO (R2)**
- [x] Channel ownership — Option C (OpenClaw transport, C# everything else) implemented as recommended
- [x] Channel priority — Telegram first, WhatsApp second
- [x] Authorization source of truth — `Principals` table, seeded from configuration
- [ ] ngrok plan — free rotating URL vs reserved domain

---

## Phase 1 — Scaffolding

- [x] `Justina.slnx`
- [x] `.gitignore`, `.dockerignore`, `Directory.Build.props`
- [x] `src/Justina.Core.Domain`
- [x] `src/Justina.Core.Application`
- [x] `src/Justina.Core.Infrastructure`
- [x] `src/Justina.Expense.Domain`
- [x] `src/Justina.Expense.Application`
- [x] `src/Justina.Expense.Infrastructure`
- [x] `src/Justina.Recruitment.Domain`
- [x] `src/Justina.Recruitment.Application`
- [x] `src/Justina.Recruitment.Infrastructure`
- [x] `src/Justina.Api`
- [x] `tests/Justina.Core.UnitTests`
- [x] `tests/Justina.Expense.UnitTests`
- [x] `tests/Justina.Recruitment.UnitTests`
- [x] `tests/Justina.ArchitectureTests`
- [x] `tests/Justina.IntegrationTests`
- [x] Warnings-as-errors in `Directory.Build.props`
- [x] CI pipeline (build, all test projects, vulnerable-package gate, compose validation, no-.env check)
- [x] `.editorconfig`

---

## Phase 2 — Docker

- [x] `docker-compose.yml` with `justina-network`
- [x] `justina-sqlserver` service (SQL Server 2022, `ACCEPT_EULA`, SA password from `.env`)
- [x] `justina-app` service + `src/Justina.Api/Dockerfile` (multi-stage, non-root, PDFium native deps)
- [x] `justina-nginx` service + `nginx.conf` + `conf.d/justina.conf`
- [x] `justina-ngrok` service
- [x] `justina-openclaw` service + `openclaw.json.template`
- [x] Volumes: `sqlserver-data`, `openclaw-config`, `justina-media`
- [x] Health checks on every service
- [x] `/health/live` and `/health/ready` endpoints
- [x] `.env.example` with every variable and no real secrets
- [x] Tool API not exposed publicly (NGINX returns 404 for `/tools/`)
- [~] Service-name connectivity verified end to end — **stack not started; SQL Server image not pulled**
- [x] How to retrieve the ngrok public URL, documented
- [ ] `docker-compose.override.yml` for local development

---

## Phase 3 — Core domain and persistence

- [x] Core primitives: `InboundMessage`, `MediaReference`, `UserContext`, `CorrelationId`, `Result<T>`
- [x] `JustinaDbContext` with `IModelConfiguration` so Core never references a domain
- [x] Entity configurations: `decimal(18,2)`, `rowversion`, `datetime2` UTC, `nvarchar(max)` JSON
- [x] Tables: Conversations, Receipts, ReceiptLineItems, ReceiptEvents, ReceiptBatches, InboundMessages, IdempotencyKeys, Principals
- [x] Unique index `(Channel, MessageId)`
- [x] Filtered unique index on `Receipts.ExternalExpenseId`
- [x] Migrations `InitialSchema` and `AddReceiptSequenceInBatch`
- [x] Guarded startup migration
- [x] `SqlServerConversationStateStore`
- [x] `SqlServerIdempotencyStore`
- [x] `SqlServerInboundMessageDeduplicator`
- [x] `EfUnitOfWork` mapping concurrency and uniqueness violations to typed conflicts
- [~] Migrations applied against a real SQL Server — **not run**

---

## Phase 4 — Expense domain

- [x] `Receipt` aggregate
- [x] `ReceiptState` with transition methods; illegal transitions throw
- [x] `Money` value object with ISO-4217 validation
- [x] `ReceiptBatch` with reading-order sequence
- [x] `ReceiptEvent` audit records written by the aggregate
- [x] Unit tests: every legal transition
- [x] Unit tests: every illegal transition rejected
- [x] Extraction retry after failure (`ExtractionFailed → Extracting`)
- [x] Missing-field names are user-facing, not property names

---

## Phase 5 — CQRS pipeline

- [x] `ICommandHandler<,>` / `IQueryHandler<,>` / `IDispatcher`
- [x] `ReceiveReceiptCommand` + handler
- [x] `ExtractReceiptCommand` + handler
- [x] `UpdateReceiptCommand` + handler
- [x] `ConfirmReceiptCommand` + handler
- [x] `CancelReceiptCommand` + handler
- [x] `SubmitExpenseCommand` + handler (retry path)
- [x] `GetReceiptQuery`, `GetReceiptStatusQuery`, `GetSessionContextQuery`
- [x] `LoggingCommandDecorator`
- [x] `ValidationCommandDecorator`
- [x] `AuthorizationCommandDecorator` (+ query equivalent)
- [x] `IdempotencyCommandDecorator`
- [x] Failures are never cached, so transient errors stay retryable
- [x] Architecture test: query handlers cannot reach `IUnitOfWork`

---

## Phase 6 — Document processing

- [x] `IDocumentProcessor` + `DocumentProcessor`
- [x] Magic-byte sniffing (JPEG, PNG, WEBP, PDF); everything else refused
- [x] Size cap, configurable, mirrored in NGINX
- [x] PdfPig parsing, integrity check, page count
- [x] Page-count cap
- [x] Text-vs-scanned classification
- [x] `PdfiumPageRenderer` rasterization fallback
- [x] Every page read; page 1 never assumed to be the whole document
- [x] Multi-receipt detection → `ReceiptBatch` + explicit question
- [x] Unit tests including corrupt, oversized, mislabelled and unsupported files

---

## Phase 7 — Vision

- [x] `IVisionProvider` abstraction
- [x] `OpenAiVisionProvider` (Responses API, strict JSON schema)
- [x] Direct-PDF submission path
- [x] Rendered-pages and extracted-text fallback paths
- [x] Extraction schema returning a list of receipt candidates
- [x] Document content attached as data, never spliced into the instruction
- [x] `ReceiptNormalizer` — text, currency, date, amount, tax, line items
- [x] Prompt-injection content kept as inert data (covered by test)
- [~] Fixture corpus of real receipts (JPEG, PNG, WEBP, poor quality) — **synthetic PDFs only**
- [~] Live provider smoke test — **not run; needs an API key**

---

## Phase 8 — Tool API

- [x] Normalized inbound envelope contract
- [x] `justina.session.context`
- [x] `justina.expense.receive_media`
- [x] `justina.expense.get_receipt`
- [x] `justina.expense.edit_receipt`
- [x] `justina.expense.confirm_receipt`
- [x] `justina.expense.cancel_receipt`
- [x] `justina.expense.retry_submission`
- [x] `justina.recruitment.search_candidates`
- [x] Shared-secret authentication, fixed-time comparison, fails closed
- [x] Tool API not routed to the internet

---

## Phase 9 — Telegram channel

- [x] `TelegramMediaDownloader` (`getFile` + file endpoint)
- [x] `TelegramResponder`
- [x] Normalization to the common envelope
- [x] Deduplication by `(Channel, MessageId)`
- [~] Text, image and PDF intake verified against live Telegram — **not run**
- [~] First full end-to-end journey — **not run**

---

## Phase 10 — OpenClaw agents

- [x] Orchestrator prompt
- [x] Intent Router prompt
- [x] Expense Agent prompt
- [x] Recruitment Agent prompt (phase 1)
- [x] Tool declarations (`justina-tools.json`)
- [x] Active-workflow rule dominates routing
- [x] Clarification on low confidence / ambiguity
- [x] Unauthorized domains removed from the candidate set
- [~] Tool registration confirmed against the pinned image — **blocked on the R3 spike**
- [ ] Routing regression tests executed

---

## Phase 11 — Expense API client

- [x] `IExpenseApiClient` contract
- [x] `ExpenseApiClient` typed `HttpClient`
- [x] Request/response mapping isolated in Infrastructure
- [x] Authentication header from configuration
- [x] Timeout (30 s default)
- [x] Retry, backoff and circuit breaker (`AddStandardResilienceHandler`)
- [x] Idempotency key header on submission
- [x] Correlation ID propagation
- [x] Error mapping to domain results
- [x] Integration tests: success, auth, validation, conflict, 5xx, timeout, invalid response, unconfigured
- [!] Real Expense API contract — **blocked on PO (R1)**

---

## Phase 12 — Journey hardening

- [x] Extraction result displayed before any API call
- [x] Edits apply to requested fields only
- [x] Receipt returns to `WAITING_CONFIRMATION` after every edit, forcing re-display
- [x] Cancel performs no API call
- [x] Duplicate confirmation creates exactly one expense
- [x] Concurrent confirmation resolved by `rowversion`
- [x] Multi-receipt batch requires per-receipt confirmation
- [x] Deterministic "next receipt" within a batch (`SequenceInBatch`)

---

## Phase 13 — WhatsApp channel

- [x] `WhatsAppMediaDownloader` (two-step Graph fetch with explicit bearer)
- [x] `WhatsAppResponder`
- [x] Retry / duplicate handling (shared with Telegram)
- [~] Webhook verification and signature checking — **configured in the OpenClaw gateway, not exercised**
- [~] Text, image and PDF intake verified against live WhatsApp — **not run**
- [~] End-to-end PDF receipt journey — **not run**

---

## Phase 14 — Security

- [x] Prompt-injection fixture — no behaviour change, no leakage
- [x] Oversized document rejected cleanly
- [x] Corrupt / malformed PDF rejected cleanly
- [x] Disallowed MIME type rejected (sniffed, not declared)
- [x] Provider and API error bodies never relayed to the user
- [x] NGINX request size limits, timeouts, security headers
- [x] Media stored outside the web root with TTL cleanup, hashed filenames
- [x] Tool API fails closed without a configured secret
- [x] Container runs as non-root
- [x] Credential scrubbing in logs and traces (`SecretScrubber`; Telegram client loggers removed)
- [x] Dependency vulnerability scan in CI
- [ ] Rate limiting on the tool API

---

## Phase 15 — Observability

- [x] Serilog structured JSON to stdout
- [x] OpenTelemetry traces and metrics with OTLP exporter
- [x] Correlation ID, Conversation ID, Channel, Command type on every command
- [x] Duration and outcome on every command
- [x] Receipt ID and state on domain events
- [~] Verified nothing sensitive is logged at runtime — **reviewed in code, not observed live**

---

## Phase 16 — Documentation

### 01-architecture
- [x] `overview.md`
- [x] `system-architecture.md`
- [x] `docker-architecture.md`
- [x] `ai-architecture.md`
- [x] `vision-architecture.md`
- [x] `integration-architecture.md`

### 02-developer
- [x] `getting-started.md`
- [x] `local-development.md`
- [x] `project-structure.md`
- [x] `csharp-architecture.md` (includes "how to add a new domain")
- [x] `openclaw.md`
- [x] `agents.md`
- [x] `tools.md`
- [x] `vision-ai.md`
- [x] `pdf-processing.md`
- [x] `channels.md`
- [x] `api-integrations.md`
- [x] `docker.md`
- [x] `configuration.md`
- [x] `security.md`
- [x] `troubleshooting.md`

### 03-qa
- [x] `test-strategy.md`, `test-environment.md`, `test-cases.md`, `receipt-testing.md`, `pdf-testing.md`,
      `telegram-testing.md`, `whatsapp-testing.md`, `agent-routing-testing.md`, `api-testing.md`,
      `security-testing.md`, `regression-testing.md`

### 04-product
- [x] `product-overview.md`, `capabilities.md`, `user-journeys.md`, `business-rules.md`,
      `receipt-workflow.md`, `confirmation-and-editing.md`, `supported-channels.md`, `domain-routing.md`,
      `error-handling.md`, `roadmap.md`

### Root
- [x] `README.md`
- [ ] Traceability matrix: requirement → plan → architecture → implementation → QA test → acceptance

---

## Phase 17 — Test pass (TESTER)

- [x] Automated suite executed: 143 tests, 0 failures
- [x] `test/test-report.md` written with real evidence and explicit NOT TESTED entries
- [~] Docker startup / shutdown / restart — **not exercised**
- [~] Agent routing — **prompts written; not exercised against a live model**
- [~] Vision against real receipt images and scanned PDFs — **not exercised**
- [~] Telegram and WhatsApp journeys — **not exercised**
- [ ] Failures looped back to CODER and re-verified — none outstanding from the automated suite

---

## Phase 18 — Final review

- [x] Implementation status reported
- [x] Documentation status reported
- [x] Testing status reported
- [x] Known issues and limitations listed
- [x] Security considerations listed
- [ ] All 15 acceptance criteria in `plan/task.md` §30 met — blocked on R1 and a live environment
- [ ] **FINAL HUMAN APPROVAL** → `PROJECT STATUS: COMPLETE`

---

## Open blockers

| ID | Blocker | Owner | Blocks |
|---|---|---|---|
| R1 | Expense API contract, base URL, auth, error format, credentials | Product Owner | Real submission; acceptance criteria 3–8, 13 |
| R2 | Recruitment API contract | Product Owner | Recruitment phase 2 |
| R3 | OpenClaw custom-tool registration mechanism | Spike | First live run of the agent layer |
| — | A machine that can run the full Docker stack and live channels | Environment | All end-to-end verification |
