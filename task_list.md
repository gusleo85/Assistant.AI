# Justina — Master Task List

Companion to `plan/task.md`. One line per deliverable, ticked only when the work actually exists in the
repository and has been verified. Section references (§) point at the master prompt.

**Legend:** `[x]` done · `[ ]` not started · `[~]` in progress · `[!]` blocked

**Current phase:** PLANNER complete → **awaiting human approval (§44)**. No production code may start.

---

## Phase 0 — Planning (PLANNER)

### 0.1 Repository inspection (§42, §61)

- [x] Inspect existing repositories / source — **empty, no commits on `main`**
- [x] Inspect existing C# architecture — none (no `.sln`, no `.csproj`)
- [x] Inspect existing Docker configuration — none
- [x] Inspect OpenClaw configuration — none in repo
- [x] Inspect available agents / tools — none
- [x] Inspect existing APIs and contracts — none present (Expense + Recruitment specs missing)
- [x] Inspect authentication and authorization — none
- [x] Inspect existing tests — none
- [x] Inspect existing documentation — `docs/` exists but empty
- [x] Verify local toolchain — .NET SDK 10.0.400, Docker 29.3.1, Compose v5.1.1

### 0.2 External capability verification (§23 requires this before implementation)

- [x] Verify OpenClaw architecture (Node.js gateway, hub-and-spoke, channel plugins, `openclaw.json`, WS control plane)
- [x] Verify OpenAI direct PDF input support and limits (100 pages / 32 MB)
- [ ] **Spike:** how custom tools register with OpenClaw (MCP vs skill vs plugin) — see §29 step 0
- [ ] **Spike:** exact OpenClaw container image/tag, volumes, config mount
- [ ] **Spike:** OpenAI structured-output schema + direct-PDF call shape against the live API

### 0.3 Plan document (§43)

- [x] Create `plan/task.md` with all 32 required sections
- [x] Record the AI ↔ C# boundary
- [x] Record the CQRS decision (Expense workflow only, hand-rolled handlers, no MediatR)
- [x] Record the Docker + network design
- [x] Record the security and idempotency design
- [x] Record risks and alternatives
- [x] Add "Decisions Required From The Product Owner" section
- [x] End with `PLAN STATUS: READY FOR HUMAN REVIEW` and stop
- [x] **Change database from PostgreSQL to SQL Server** (team's existing platform) — §4, §6, §21, §22, §24, §25, §26, §29, §32 updated; risk R11 added
- [x] Create `task_list.md` (this file)

### 0.4 Approval gate (§44)

- [ ] **HUMAN APPROVAL of `plan/task.md`** — required before any code
- [!] Expense API contract, base URL, auth scheme, error format, sandbox credentials — **blocked on PO (R1)**
- [!] Recruitment API contract — **blocked on PO (R2)**, or confirm routing-only for phase 1
- [ ] Confirm channel ownership: Option C hybrid (recommended) vs A vs B
- [ ] Confirm channel priority: Telegram first, WhatsApp second
- [ ] Confirm authorization source of truth for phase 1
- [ ] Confirm ngrok plan (free rotating URL vs reserved domain)

---

## Phase 1 — Scaffolding (CODER)

- [ ] `Justina.sln`
- [ ] `.gitignore`, `.dockerignore`, `.editorconfig`, `Directory.Build.props`
- [ ] `src/Justina.Core.Domain`
- [ ] `src/Justina.Core.Application`
- [ ] `src/Justina.Core.Infrastructure`
- [ ] `src/Justina.Expense.Domain`
- [ ] `src/Justina.Expense.Application`
- [ ] `src/Justina.Expense.Infrastructure`
- [ ] `src/Justina.Recruitment.Domain`
- [ ] `src/Justina.Recruitment.Application`
- [ ] `src/Justina.Recruitment.Infrastructure`
- [ ] `src/Justina.Api`
- [ ] `tests/Justina.Core.UnitTests`
- [ ] `tests/Justina.Expense.UnitTests`
- [ ] `tests/Justina.Recruitment.UnitTests`
- [ ] `tests/Justina.ArchitectureTests`
- [ ] `tests/Justina.IntegrationTests`
- [ ] Analyzers + warnings-as-errors in `Directory.Build.props`
- [ ] CI pipeline (build, test, `dotnet list package --vulnerable`)

---

## Phase 2 — Docker skeleton (§3, §6, §7, §8, §9)

- [ ] `docker-compose.yml` with `justina-network`
- [ ] `docker-compose.override.yml` for local development
- [ ] `justina-sqlserver` service (`mcr.microsoft.com/mssql/server:2022-latest`, `ACCEPT_EULA`, SA password from `.env`)
- [ ] `justina-app` service + `src/Justina.Api/Dockerfile`
- [ ] `justina-nginx` service + `docker/nginx/nginx.conf` + `docker/nginx/conf.d/justina.conf`
- [ ] `justina-ngrok` service
- [ ] `justina-openclaw` service + `docker/openclaw/openclaw.json.template`
- [ ] Volumes: `sqlserver-data`, `openclaw-config`, `justina-media`
- [ ] Health checks on every service
- [ ] `/health/live` and `/health/ready` endpoints
- [ ] `.env.example` with every variable, **no real secrets** (§39)
- [ ] Verify service-name connectivity end to end (no `localhost` between containers)
- [ ] Document how to retrieve the current ngrok public URL (§9)

---

## Phase 3 — Core domain and persistence (§24)

- [ ] Core primitives: `ChannelMessage`, `MediaRef`, `UserContext`, `CorrelationId`, `Result<T>`
- [ ] `JustinaDbContext` (EF Core 10 + `Microsoft.EntityFrameworkCore.SqlServer`)
- [ ] Entity configurations: `decimal(18,2)` money, `rowversion`, `datetime2` UTC, `nvarchar(max)` JSON
- [ ] Tables: `Conversations`, `Receipts`, `ReceiptLineItems`, `ReceiptEvents`, `InboundMessages`, `IdempotencyKeys`, `Principals`
- [ ] Unique index `(Channel, MessageId)` on `InboundMessages`
- [ ] Filtered unique index on `Receipts.ExternalExpenseId`
- [ ] Initial EF migration + guarded startup migration step
- [ ] `SqlServerConversationStateStore`
- [ ] `SqlServerIdempotencyStore`

---

## Phase 4 — Expense domain (§30)

- [ ] `Receipt` aggregate
- [ ] `ReceiptState` enum + transition methods (illegal transitions throw)
- [ ] Value objects: `Money`, `ReceiptDate`, `MerchantName`, `TaxAmount`
- [ ] `ReceiptBatch` (multi-receipt support, §25)
- [ ] `ReceiptEvent` audit records
- [ ] Unit tests: every legal transition
- [ ] Unit tests: every illegal transition rejected

---

## Phase 5 — CQRS pipeline (§14)

- [ ] `ICommandHandler<,>` / `IQueryHandler<,>` abstractions
- [ ] `ReceiveReceiptCommand` + handler
- [ ] `ExtractReceiptCommand` + handler
- [ ] `UpdateReceiptCommand` + handler
- [ ] `ConfirmReceiptCommand` + handler
- [ ] `CancelReceiptCommand` + handler
- [ ] `SubmitExpenseCommand` + handler
- [ ] `GetReceiptQuery` + handler
- [ ] `GetReceiptStatusQuery` + handler
- [ ] `GetConversationQuery` + handler
- [ ] `LoggingDecorator`
- [ ] `ValidationDecorator`
- [ ] `AuthorizationDecorator` (§34)
- [ ] `IdempotencyDecorator` (§33)
- [ ] Test: queries never call `SaveChanges`

---

## Phase 6 — Document processing (§22, §24)

- [ ] `IDocumentProcessor` + `DocumentProcessor`
- [ ] MIME sniffing by magic bytes (JPEG, PNG, WEBP, PDF); reject everything else
- [ ] File size cap (configurable, default 20 MB) enforced in C# and in NGINX
- [ ] `PdfPigTextExtractor` — parse, integrity check, page count
- [ ] Page count cap (configurable, default 20)
- [ ] Text-PDF vs scanned-PDF classification
- [ ] `PdfiumPageRenderer` — page rasterization fallback
- [ ] Multi-page handling (never assume page 1 is the whole receipt)
- [ ] Multi-receipt detection → `ReceiptBatch` + explicit user question (§25)

---

## Phase 7 — Vision (§20, §21)

- [ ] `IVisionProvider` abstraction
- [ ] `OpenAiVisionProvider`
- [ ] Direct-PDF submission path
- [ ] Local extraction/rasterization fallback path
- [ ] Structured-output extraction schema (receipt candidate list)
- [ ] Document content passed as data, never as instruction (§38)
- [ ] `IReceiptValidator` — required fields, date, currency, amount, tax, types (§27)
- [ ] `ReceiptNormalizer`
- [ ] Fixture corpus: JPEG, PNG, WEBP, text PDF, scanned PDF, multi-page, multi-receipt, poor quality, corrupt, oversized
- [ ] Golden tests against a stubbed provider (deterministic, offline)

---

## Phase 8 — Tool API (§16, §31)

- [ ] Normalized inbound envelope contract (§35)
- [ ] `justina.session.context`
- [ ] `justina.expense.receive_media`
- [ ] `justina.expense.get_receipt`
- [ ] `justina.expense.edit_receipt`
- [ ] `justina.expense.confirm_receipt`
- [ ] `justina.expense.cancel_receipt`
- [ ] Shared-secret authentication on the Tool API
- [ ] Tool API not routed to the internet by NGINX

---

## Phase 9 — Telegram channel (§37)

- [ ] `TelegramMediaDownloader` (`getFile` + bot file endpoint)
- [ ] `TelegramResponder`
- [ ] Normalization to the common envelope
- [ ] Deduplication by `update_id` / `message_id`
- [ ] Text, image and PDF intake verified
- [ ] First full end-to-end journey working

---

## Phase 10 — OpenClaw agents (§17, §18)

- [ ] Orchestrator agent
- [ ] Intent Router agent
- [ ] Expense Agent
- [ ] Recruitment Agent (phase 1: routes correctly, reports not-yet-available)
- [ ] Justina tool registration in OpenClaw
- [ ] Active-workflow rule dominates routing
- [ ] Clarification on low confidence / ambiguity
- [ ] Unauthorized domains removed from the candidate set
- [ ] Routing regression tests

---

## Phase 11 — Expense API client (§31, §32)

- [ ] `IExpenseApiClient` contract
- [ ] `ExpenseApiClient` typed `HttpClient`
- [ ] Request/response mapping (Infrastructure only)
- [ ] Authentication header injection from configuration
- [ ] Timeout (default 30 s)
- [ ] Retry with exponential backoff + jitter (transient only)
- [ ] Circuit breaker
- [ ] Idempotency key header on submission
- [ ] Correlation ID propagation
- [ ] Error mapping to domain results
- [ ] Integration tests against WireMock: success, timeout, 5xx, invalid response, duplicate submit
- [!] Swap the provisional contract for the real Expense API spec — **blocked on PO (R1)**

---

## Phase 12 — Journey hardening (§28, §29, §33)

- [ ] Extraction result displayed before any API call
- [ ] Natural-language edit applied to requested fields only
- [ ] Full receipt re-displayed + confirmation re-asked after every edit
- [ ] Cancel path performs no API call
- [ ] Duplicate confirmation creates exactly one expense
- [ ] Concurrent confirmation resolved by `rowversion` conflict
- [ ] Multi-receipt batch requires per-receipt confirmation

---

## Phase 13 — WhatsApp channel (§36)

- [ ] Webhook verification
- [ ] Signature verification (`X-Hub-Signature-256`)
- [ ] `WhatsAppMediaDownloader`
- [ ] `WhatsAppResponder`
- [ ] Text, image and PDF intake verified
- [ ] Retry / duplicate handling
- [ ] End-to-end PDF receipt journey (§23)

---

## Phase 14 — Security (§38)

- [ ] Prompt-injection fixtures (malicious receipt text) — no behaviour change, no leakage
- [ ] Oversized document rejected cleanly
- [ ] Corrupt / malformed PDF rejected cleanly
- [ ] Disallowed MIME type rejected
- [ ] Log redactor for secrets and `Authorization` headers
- [ ] Secret-leak audit: no credentials in prompts, tool arguments, logs or user messages
- [ ] NGINX request size limits, timeouts, security headers
- [ ] Media stored outside web root with TTL cleanup
- [ ] Dependency vulnerability scan green

---

## Phase 15 — Observability (§40)

- [ ] Serilog structured JSON logging to stdout
- [ ] OpenTelemetry traces and metrics
- [ ] Correlation ID, Conversation ID, Message ID on every log and span
- [ ] Agent name, tool name, external request ID, duration
- [ ] Receipt ID and state on domain events
- [ ] Verify nothing sensitive is logged

---

## Phase 16 — Documentation (§49–§53)

### 01-architecture
- [ ] `docs/01-architecture/overview.md`
- [ ] `docs/01-architecture/system-architecture.md`
- [ ] `docs/01-architecture/docker-architecture.md`
- [ ] `docs/01-architecture/ai-architecture.md`
- [ ] `docs/01-architecture/vision-architecture.md`
- [ ] `docs/01-architecture/integration-architecture.md`

### 02-developer
- [ ] `docs/02-developer/getting-started.md`
- [ ] `docs/02-developer/local-development.md`
- [ ] `docs/02-developer/project-structure.md`
- [ ] `docs/02-developer/csharp-architecture.md`
- [ ] `docs/02-developer/openclaw.md`
- [ ] `docs/02-developer/agents.md`
- [ ] `docs/02-developer/tools.md`
- [ ] `docs/02-developer/vision-ai.md`
- [ ] `docs/02-developer/pdf-processing.md`
- [ ] `docs/02-developer/channels.md`
- [ ] `docs/02-developer/api-integrations.md`
- [ ] `docs/02-developer/docker.md`
- [ ] `docs/02-developer/configuration.md`
- [ ] `docs/02-developer/security.md`
- [ ] `docs/02-developer/troubleshooting.md`
- [ ] "How to add a new domain" guide (§51)

### 03-qa
- [ ] `docs/03-qa/test-strategy.md`
- [ ] `docs/03-qa/test-environment.md`
- [ ] `docs/03-qa/test-cases.md`
- [ ] `docs/03-qa/receipt-testing.md`
- [ ] `docs/03-qa/pdf-testing.md`
- [ ] `docs/03-qa/telegram-testing.md`
- [ ] `docs/03-qa/whatsapp-testing.md`
- [ ] `docs/03-qa/agent-routing-testing.md`
- [ ] `docs/03-qa/api-testing.md`
- [ ] `docs/03-qa/security-testing.md`
- [ ] `docs/03-qa/regression-testing.md`

### 04-product
- [ ] `docs/04-product/product-overview.md`
- [ ] `docs/04-product/capabilities.md`
- [ ] `docs/04-product/user-journeys.md`
- [ ] `docs/04-product/business-rules.md`
- [ ] `docs/04-product/receipt-workflow.md`
- [ ] `docs/04-product/confirmation-and-editing.md`
- [ ] `docs/04-product/supported-channels.md`
- [ ] `docs/04-product/domain-routing.md`
- [ ] `docs/04-product/error-handling.md`
- [ ] `docs/04-product/roadmap.md`

### Root
- [ ] `README.md`
- [ ] Traceability matrix: requirement → plan → architecture → implementation → QA test → acceptance (§55)

---

## Phase 17 — Test pass (TESTER, §46, §47)

### Docker
- [ ] Startup / shutdown / restart
- [ ] Network + service-name resolution
- [ ] Configuration
- [ ] Health checks
- [ ] Logs

### Agent routing
- [ ] Expense request → Expense Agent
- [ ] Recruitment request → Recruitment Agent
- [ ] Ambiguous request → clarification
- [ ] Active workflow → correct existing agent

### Vision
- [ ] JPEG · [ ] PNG · [ ] PDF
- [ ] Text PDF · [ ] Scanned PDF · [ ] Multi-page PDF
- [ ] Multiple receipts
- [ ] Poor-quality document
- [ ] Invalid document
- [ ] Vision failure handling

### Receipt
- [ ] Extraction · [ ] Validation · [ ] Display
- [ ] Edit · [ ] Re-display · [ ] Confirmation
- [ ] Cancellation · [ ] Submission · [ ] Duplicate prevention

### API
- [ ] Authentication · [ ] Authorization · [ ] Timeout
- [ ] Retry · [ ] Failure · [ ] Invalid response

### Channels
- [ ] Telegram: text, image, PDF, edit, confirm, cancel
- [ ] WhatsApp: text, image, PDF, edit, confirm, cancel

### Security
- [ ] Unauthorized access
- [ ] Prompt injection
- [ ] Malicious PDF
- [ ] Oversized document
- [ ] Secret leakage

### Report
- [ ] `test/test-report.md` written with real evidence (never fabricated, §47)
- [ ] All failures looped back to CODER and re-verified (§48)

---

## Phase 18 — Final review (§58, §59)

- [ ] Implementation status reported
- [ ] Documentation status reported (Developer / QA / Product / Architecture)
- [ ] Testing status reported
- [ ] Known issues listed
- [ ] Known limitations listed
- [ ] Security considerations listed
- [ ] Deployment status listed
- [ ] All 15 acceptance criteria in `plan/task.md` §30 met
- [ ] **FINAL HUMAN APPROVAL** → `PROJECT STATUS: COMPLETE`

---

## Open blockers

| ID | Blocker | Owner | Blocks |
|---|---|---|---|
| R1 | Expense API contract, base URL, auth, error format, credentials | Product Owner | Phase 11, acceptance criteria 3–8, 13 |
| R2 | Recruitment API contract | Product Owner | Recruitment phase 2 |
| R3 | OpenClaw custom-tool registration mechanism | Planner spike | Phase 8, Phase 10 |
| — | Plan approval (§44) | Product Owner | **Everything from Phase 1 onward** |
