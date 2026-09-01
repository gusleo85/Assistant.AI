# PDF and Media Processing

C# owns document processing. Nothing about a user's file is taken on trust.

`src/Justina.Core.Infrastructure/Documents/`:

| File | Role |
|---|---|
| `MediaTypeSniffer.cs` | Identifies the real type from magic bytes |
| `DocumentProcessor.cs` | Validation, parsing, classification, page handling |
| `PdfiumPageRenderer.cs` | Rasterization (the only file that touches PDFium) |
| `DocumentProcessingOptions.cs` | Every limit, configurable |

## The pipeline

```text
bytes + declared MIME
      ▼
 empty?                          → document_unreadable
 over MaxBytes?                  → media_too_large
 sniff magic bytes               → unsupported_media if unrecognized
      ├── image → done (1 page)
      └── pdf
            open with PdfPig     → document_unreadable if it will not parse
            page count           → too_many_pages over MaxPages
            read every page's text
            classify: avg chars/page < threshold → ScannedPdf, else TextPdf
            decide direct upload vs local fallback
            rasterize only if needed
```

## Type sniffing

```csharp
"%PDF"                                  → application/pdf
FF D8 FF                                → image/jpeg
89 50 4E 47 0D 0A 1A 0A                 → image/png
"RIFF" .... "WEBP"                      → image/webp
```

The declared MIME type is attacker-controlled. A mismatch is logged at Information — channels mislabel
routinely — and the **sniffed** type is what the pipeline acts on. A file claiming `application/pdf` that
is really a PNG is processed as a PNG; a file that is really an executable is refused.

## Limits

| Option | Default | Why |
|---|---|---|
| `MaxBytes` | 20 MB | Below the provider's 32 MB; NGINX caps at 25 MB |
| `MaxPages` | 20 | Bounds parse cost on hostile input |
| `ScannedTextThresholdPerPage` | 80 chars | Below this the text layer is not usable |
| `ProviderMaxDirectUploadBytes` | 32 MB | The provider's documented limit |
| `ProviderMaxDirectUploadPages` | 100 | The provider's documented limit |
| `AllowDirectPdfUpload` | `true` | Turn off to force the local path |
| `RenderDpi` | 200 | Legible receipt text without excessive size |
| `MediaRetention` | 6 hours | How long downloaded media survives |

All under `DocumentProcessing:` in configuration.

## Text vs scanned

Average extracted characters per page decides. It is a heuristic, and deliberately a cheap one: getting it
wrong costs an unnecessary rasterization or a weaker extraction, never a wrong value, because the user
confirms the result either way.

A mixed document — some pages scanned, some not — classifies by the average. Direct provider upload
handles that case well, which is another reason it is the default.

## Every page, always

```csharp
for (var number = 1; number <= pageCount; number++)
```

Never just page one. A receipt may start on page 2, and a PDF may hold several receipts — see
[vision-ai.md](vision-ai.md) for how several become several expenses rather than one.

## Rasterization

Only when the provider cannot read the file itself **and** the text layer is unusable. `PdfiumPageRenderer`
renders through PDFium (via PDFtoImage) to PNG in memory.

The container installs `libfontconfig1` and `libfreetype6`, which PDFium and SkiaSharp need on Linux.
Without them, rendering fails at runtime with a native load error — see
[troubleshooting.md](troubleshooting.md).

Ghostscript and ImageMagick were deliberately not used: both are large native attack surfaces, and this
pipeline exists specifically to process untrusted files.

A rasterization failure is a `Result` failure, not an exception.

## Media storage

`FileSystemMediaStore` writes to `MediaStore:RootPath` (`/var/justina/media`, its own volume, outside
anything served). File names are the SHA-256 of the channel's media id, so a hostile identifier cannot
traverse out of the directory. A sidecar `.json` holds the MIME type and file name.

`MediaCleanupService` deletes files older than `MediaRetention` every hour, and never takes the service
down if a delete fails.

## Testing

`TestPdf` in `tests/Justina.Core.UnitTests` builds structurally valid PDFs in memory — with text, without
text, any page count — including a correct xref table, so tests exercise the real parse path rather than
PdfPig's lenient recovery. Prefer it to binary fixtures.

`IPdfPageRenderer` is substitutable, so the fallback path is testable with no native renderer.
