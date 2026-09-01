# Configuration

## How it flows

```text
.env  ──▶  docker-compose.yml  ──▶  container environment  ──▶  .NET configuration  ──▶  typed options
```

Double underscore maps to a configuration section: `OpenAiVision__ApiKey` sets `OpenAiVision:ApiKey`.
`appsettings.json` holds structure and safe defaults, with **every secret blank**. Real values only ever
come from the environment.

`.env` is git-ignored. `.env.example` is the committed template. Never commit a real credential.

## Required to start

Compose fails fast with a message if these are missing, rather than starting a half-configured stack:

| Variable | Notes |
|---|---|
| `MSSQL_SA_PASSWORD` | Must meet the SQL Server policy: 8+ chars, upper, lower, digit, symbol |
| `JUSTINA_TOOL_SECRET` | Shared between OpenClaw and the app. `openssl rand -hex 32` |
| `NGROK_AUTHTOKEN` | Public ingress |

## Everything else

| Variable | Effect if blank |
|---|---|
| `OPENAI_API_KEY` | Documents cannot be read; extraction returns `vision_failed` |
| `OPENAI_VISION_MODEL` | Defaults to `gpt-4.1` |
| `TELEGRAM_BOT_TOKEN` | Telegram returns `not_available` |
| `WHATSAPP_ACCESS_TOKEN`, `WHATSAPP_PHONE_NUMBER_ID` | WhatsApp returns `not_available` |
| `WHATSAPP_APP_SECRET`, `WHATSAPP_WEBHOOK_VERIFY_TOKEN` | Used by the gateway for webhook verification |
| `EXPENSE_API_URL`, `EXPENSE_API_KEY` | Submission returns `not_available` |
| `RECRUITMENT_API_URL`, `RECRUITMENT_API_KEY` | Expected blank in phase 1 |
| `OTEL_EXPORTER_OTLP_ENDPOINT` | Traces and metrics are not exported |

Every one of these degrades to a **typed, explained refusal**. Nothing crashes on startup for a missing
optional credential.

## Sections and their options classes

| Section | Class |
|---|---|
| `ConnectionStrings:Justina` | — (used directly by `AddDbContext`) |
| `Database:MigrateOnStartup` | — (read directly; default `true`) |
| `ToolApi` | `ToolApiOptions` |
| `DocumentProcessing` | `DocumentProcessingOptions` |
| `MediaStore` | `MediaStoreOptions` |
| `OpenAiVision` | `OpenAiVisionOptions` |
| `Telegram` | `TelegramOptions` |
| `WhatsApp` | `WhatsAppOptions` |
| `ExpenseApi` | `ExpenseApiOptions` |
| `RecruitmentApi` | `RecruitmentApiOptions` |

Options are bound once in a `Configure<T>` call and injected as `IOptions<T>`. Domain and application code
never sees `IConfiguration` — an architecture test enforces that.

## Tuning worth knowing

**Document limits** — `DocumentProcessing:MaxBytes` (20 MB) and `MaxPages` (20). If you raise `MaxBytes`,
raise `client_max_body_size` in `docker/nginx/nginx.conf` too, or NGINX rejects the upload first.

**Scanned-PDF threshold** — `ScannedTextThresholdPerPage` (80 chars). Lower it if text PDFs with sparse
receipts are being rasterized unnecessarily.

**Direct PDF upload** — `AllowDirectPdfUpload: false` forces the local extraction/rasterization path,
which is useful for testing the fallback.

**Media retention** — `MediaRetention` (6 hours). Untrusted user files should not linger.

## Local development

Environment variables in your shell work exactly like container ones:

```bash
export ConnectionStrings__Justina="Server=localhost,1433;Database=Justina;User Id=sa;Password=…;TrustServerCertificate=True"
export ToolApi__SharedSecret="dev-secret"
export MediaStore__RootPath="$PWD/.media"
```

Or use `dotnet user-secrets` for the API project. `appsettings.Development.json` and
`appsettings.Local.json` are git-ignored.

## Adding an option

1. Add the property with a safe default to the options class.
2. Add it to `appsettings.json` (blank if secret).
3. If it needs to be set per deployment, add it to `docker-compose.yml` and `.env.example` with a comment
   saying what it does and what happens if it is blank.
