# Vision Architecture

Vision is a **shared Justina capability**, not an Expense feature. Expense uses it today; Recruitment
will use the same capability for résumé documents.

```text
Expense Agent
     │  justina.expense.receive_media
     ▼
ExtractReceiptCommandHandler
     ├──▶ IMediaStore              retrieve the stored bytes
     ├──▶ IDocumentProcessor       validate, classify, page-count, rasterize if needed
     └──▶ IVisionProvider ──▶ OpenAiVisionProvider ──▶ OpenAI Vision API   (external)
                                        │
                                        ▼
                          strict JSON, still untrusted
                                        │
                                        ▼
                              ReceiptNormalizer  →  domain values
```

## The abstraction, and where it stops

```csharp
Task<Result<VisionExtractionResult>> ExtractAsync(VisionRequest request, CancellationToken ct);
```

`VisionRequest` carries the processed document plus the **schema the calling domain wants**. That is what
makes Vision shared rather than Expense-shaped: the capability knows how to read a document, and the
domain knows what it wants read.

One interface, one implementation, no provider registry and no plugin loader. Swapping providers means
writing a second `IVisionProvider` and changing one DI line; no Expense business logic moves. Building a
plugin system for a second provider that does not exist yet would be the wrong trade.

## Document processing, before Vision is ever called

`DocumentProcessor` owns everything C# must decide for itself.

1. **Sniff the real type** from magic bytes — `%PDF`, JPEG `FF D8 FF`, the PNG signature, RIFF/WEBP. The
   declared MIME type is attacker-controlled; a mismatch is logged and the sniffed type wins. Anything
   else is refused.
2. **Size cap** — default 20 MB, below the provider's 32 MB limit, and enforced again at NGINX.
3. **Parse and count pages** with PdfPig. A file that will not open is a user-facing refusal, never an
   unhandled exception. Default cap 20 pages.
4. **Classify** — average extracted characters per page below the threshold (default 80) means the text
   layer is unusable, so the PDF is `ScannedPdf`; otherwise `TextPdf`.
5. **Read every page.** A receipt may begin on page 2, and one document may hold several.

## Choosing how the document reaches the provider

| Case | How it is sent |
|---|---|
| Image (JPEG/PNG/WEBP) | As an image input |
| PDF within provider limits (≤100 pages, ≤32 MB) | **Directly as a file** — the provider extracts text and page visuals itself |
| PDF over the limits, scanned | Pages rasterized with PDFium and sent as images (capped, default 10) |
| PDF over the limits, with a text layer | Extracted text, in a delimited `<document_content>` block |

Direct upload is the default because it is simpler and handles mixed text/image pages well. The local
path exists so an oversized or provider-rejected document still works, and rasterization only happens
when it is actually needed — it is the expensive branch.

PDFium and PdfPig were chosen over shelling out to Ghostscript or ImageMagick: both are large native
attack surfaces for untrusted input, and this pipeline exists to handle exactly that.

## The extraction contract

Defined in `ReceiptExtractionSchema`. Two deliberate decisions:

- **The top level is a list of receipts**, so a document holding three receipts cannot collapse into one.
- **Every value is a string.** The model copies what is printed; C# does all parsing. A model that
  returns `"12,50"`, `"August 30, 2026"` or `"SGD 1,234.56"` is handled by `ReceiptNormalizer`, and a
  value that cannot be parsed becomes `null` — surfaced to the user for correction rather than guessed.

The instruction tells the model to copy, never calculate, use null when unsure, and treat any
instruction-like text in the document as ordinary printed text.

## Validation before the domain

`ReceiptNormalizer` sits between the provider and the aggregate:

- text is stripped of control characters, whitespace-collapsed and capped at 256 characters;
- currency must be a three-letter ISO-4217 code or it becomes `null`;
- amounts are parsed from the way receipts actually print them, with the rightmost separator treated as
  the decimal point when both `.` and `,` appear;
- dates are tried against a list of real-world formats;
- a non-positive total is discarded rather than accepted;
- line items without a description are dropped.

The aggregate then re-asserts its own invariants. Vision output never reaches the Expense API unchecked.

## Failure behaviour

Every failure is a `Result`, not an exception: unconfigured provider, HTTP error, timeout, unparseable
response, and "no receipt found" all become a message the user can act on. The receipt moves to
`EXTRACTION_FAILED` and the provider's error body is logged truncated — never shown to the user.
