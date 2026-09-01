# Local Development

## The fast loop

Most work needs neither Docker nor network access.

```bash
dotnet build Justina.slnx
dotnet test tests/Justina.Expense.UnitTests
```

The full suite:

```bash
for p in tests/*/; do dotnet test "$p" --nologo -v q; done
```

Warnings are errors (`Directory.Build.props`). That is intentional — an unused variable or a nullability
hole in this codebase is usually a real mistake.

## Running the app against a local SQL Server

You do not need the whole stack to work on the C# layer.

```bash
docker compose up -d justina-sqlserver

export ConnectionStrings__Justina="Server=localhost,1433;Database=Justina;User Id=sa;Password=<your sa password>;TrustServerCertificate=True"
export ToolApi__SharedSecret="dev-secret"
export MediaStore__RootPath="$PWD/.media"

dotnet run --project src/Justina.Api
```

Migrations apply at startup. Then drive the tool API with `curl` — see
[getting-started.md](getting-started.md#5-try-a-tool-call-directly).

## Migrations

```bash
dotnet ef migrations add <Name> \
  --project src/Justina.Core.Infrastructure \
  --startup-project src/Justina.Api \
  --output-dir Persistence/Migrations
```

`DesignTimeDbContextFactory` in `Justina.Api/Hosting` supplies the same domain model configurations the
runtime uses, so a generated migration matches what the application actually maps. If you add a domain,
add its `IModelConfiguration` there too — otherwise its tables silently vanish from migrations.

Review generated SQL before committing. The things worth checking: `decimal(18,2)` on money, `rowversion`
on `Receipts`, and that the unique index on `ExternalExpenseId` is still filtered.

## Working on document processing

`TestPdf` in `tests/Justina.Core.UnitTests` builds structurally valid PDFs in memory — with a text layer,
without one, and with any number of pages. Prefer it to committing binary fixtures.

Rasterization is behind `IPdfPageRenderer`, so the pipeline is testable without a native renderer. Only
`PdfiumPageRenderer` touches PDFium.

## Working on Vision

`IVisionProvider` is the seam. Substitute it to test extraction, normalization and the multi-receipt path
without spending tokens. Only reach for the real provider when you are changing the request shape itself.

The API key is read from `OpenAiVision:ApiKey`. With no key configured, the provider returns a typed
failure and logs an error — it does not throw.

## Working on the agents

Agent prompts in `docker/openclaw/workspace/` are mounted read-only. Editing one and restarting
`justina-openclaw` is enough:

```bash
docker compose restart justina-openclaw
```

Treat these as source. A change to the Intent Router's rules changes system behaviour as surely as a
change to a command handler.

## Useful commands

```bash
docker compose logs -f justina-app
docker compose logs -f justina-openclaw

# current ngrok public URL
curl -s http://localhost:4040/api/tunnels | jq -r '.tunnels[0].public_url'

# validate compose without starting anything
docker compose config >/dev/null && echo ok

# reset the database completely
docker compose down -v
```

`docker compose down -v` deletes the SQL Server volume and every receipt in it.

## Conventions

- One public type per file, except tightly-coupled records that are read together.
- Comments explain **why**, never what. If a comment restates the code, delete it.
- Constructor injection everywhere; no service location outside `Dispatcher`, which needs it by design.
- `ConfigureAwait(false)` in library code.
- New behaviour arrives with a test. The state machine and the idempotency rules in particular are
  covered directly, not incidentally.
