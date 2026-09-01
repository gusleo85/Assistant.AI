# Project Structure

```text
Justina.slnx                    solution (.NET 10 slnx format)
Directory.Build.props           shared build settings, warnings-as-errors
docker-compose.yml              the whole Justina environment
.env.example                    configuration template (copy to .env)

src/
  Justina.Core.Domain           shared primitives — references nothing
  Justina.Core.Application      shared abstractions + CQRS plumbing + decorators
  Justina.Core.Infrastructure   EF Core, SQL Server stores, documents, Vision, channels
  Justina.Expense.Domain        Receipt aggregate and its state machine
  Justina.Expense.Application   commands, queries, validation, normalization
  Justina.Expense.Infrastructure EF mappings, repository, Expense API client
  Justina.Recruitment.Domain    search criteria (phase 1)
  Justina.Recruitment.Application search use case + IRecruitmentApiClient
  Justina.Recruitment.Infrastructure Recruitment API client (phase 1 stub)
  Justina.Api                   ASP.NET Core host, Tool API, DI composition root

tests/
  Justina.ArchitectureTests     layering and domain-isolation rules
  Justina.Core.UnitTests        documents, decorators
  Justina.Expense.UnitTests     state machine, normalization, submission
  Justina.Recruitment.UnitTests criteria and routing behaviour
  Justina.IntegrationTests      Expense API client against a WireMock stub

docker/
  nginx/                        proxy configuration
  openclaw/agents/              agent prompts — review these like code
  openclaw/tools/               tool declarations exposed to agents
  openclaw/openclaw.json.template gateway configuration template

docs/                           this documentation
plan/task.md                    the approved architecture plan
task_list.md                    the delivery checklist
test/test-report.md             QA results
```

## Reading order for a new developer

1. `src/Justina.Expense.Domain/Receipt.cs` — the state machine is the correctness core; everything else
   exists to protect it.
2. `src/Justina.Expense.Application/Commands/` — the use cases, one file per concern.
3. `src/Justina.Core.Application/Messaging/` — the CQRS contracts and the decorator pipeline.
4. `src/Justina.Api/Tools/ToolEndpoints.cs` — the surface the AI actually calls.
5. `docker/openclaw/agents/` — how the AI is instructed to use it.

## Notable files

| File | Why it matters |
|---|---|
| `Core.Domain/Results/Result.cs` | Expected refusals are values, not exceptions |
| `Core.Domain/Results/Error.cs` | The stable error codes the agent relays |
| `Core.Application/Messaging/HandlerRegistration.cs` | The exact decorator pipeline, in one readable method |
| `Core.Infrastructure/Persistence/JustinaDbContext.cs` | How domains contribute mappings without Core knowing them |
| `Core.Infrastructure/Documents/DocumentProcessor.cs` | Every check applied to untrusted media |
| `Expense.Application/Receipts/ReceiptSubmissionService.cs` | The single path to an external expense |
| `Expense.Infrastructure/Api/ExpenseApiClient.cs` | The provisional external contract, isolated |

## Dependency rules

```text
Api ──▶ *.Infrastructure ──▶ *.Application ──▶ *.Domain
                                    │
                            Core.Application ──▶ Core.Domain
```

- `*.Domain` references nothing outside Core.Domain.
- `*.Application` references its own Domain plus Core.Application. No EF, no `HttpClient`, no
  `IConfiguration`.
- `*.Infrastructure` implements Application interfaces and is the only place SDKs appear.
- `Justina.Api` is the only composition root.
- `Expense.*` and `Recruitment.*` never reference each other.

These are enforced by `tests/Justina.ArchitectureTests`, so a violation fails the build rather than a
review.
