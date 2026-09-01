using Justina.Core.Application.Documents;
using Justina.Core.Domain.Results;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using UglyToad.PdfPig;

namespace Justina.Core.Infrastructure.Documents;

/// <summary>
/// Validates and inspects untrusted media (§24). Every failure path returns a <see cref="Result"/> the user
/// can be told about; nothing here throws for bad input, because bad input is expected.
/// </summary>
public sealed class DocumentProcessor(
    IPdfPageRenderer renderer,
    IOptions<DocumentProcessingOptions> options,
    ILogger<DocumentProcessor> logger)
    : IDocumentProcessor
{
    private readonly DocumentProcessingOptions _options = options.Value;

    public Task<Result<ProcessedDocument>> ProcessAsync(
        byte[] content,
        string declaredMimeType,
        string? fileName,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);

        if (content.Length == 0)
        {
            return Fail(ErrorCodes.DocumentUnreadable, "That file appears to be empty.");
        }

        if (content.LongLength > _options.MaxBytes)
        {
            var limitMb = _options.MaxBytes / (1024 * 1024);
            return Fail(ErrorCodes.MediaTooLarge, $"That file is larger than the {limitMb} MB limit.");
        }

        var actualMimeType = MediaTypeSniffer.Sniff(content);

        if (actualMimeType is null)
        {
            return Fail(
                ErrorCodes.UnsupportedMedia,
                "I can only read JPEG, PNG, WEBP images and PDF documents.");
        }

        if (!string.Equals(actualMimeType, declaredMimeType, StringComparison.OrdinalIgnoreCase))
        {
            // Not fatal — channels routinely mislabel — but the sniffed type is the one we trust.
            logger.LogInformation(
                "Declared media type {Declared} did not match sniffed type {Actual}",
                declaredMimeType,
                actualMimeType);
        }

        return actualMimeType == MediaTypeSniffer.Pdf
            ? Task.FromResult(ProcessPdf(content, fileName, cancellationToken))
            : Task.FromResult(ProcessImage(content, actualMimeType, fileName));
    }

    private static Result<ProcessedDocument> ProcessImage(byte[] content, string mimeType, string? fileName) =>
        Result.Success(new ProcessedDocument(
            DocumentKind.Image,
            mimeType,
            fileName,
            content.LongLength,
            PageCount: 1,
            content,
            Pages: [new DocumentPage(1, null, null)],
            SupportsDirectProviderUpload: false));

    private Result<ProcessedDocument> ProcessPdf(byte[] content, string? fileName, CancellationToken cancellationToken)
    {
        int pageCount;
        List<DocumentPage> pages;
        int totalCharacters;

        try
        {
            using var document = PdfDocument.Open(content);
            pageCount = document.NumberOfPages;

            if (pageCount == 0)
            {
                return Result.Failure<ProcessedDocument>(
                    ErrorCodes.DocumentUnreadable,
                    "That PDF has no pages.");
            }

            if (pageCount > _options.MaxPages)
            {
                return Result.Failure<ProcessedDocument>(
                    ErrorCodes.TooManyPages,
                    $"That PDF has {pageCount} pages; I can process up to {_options.MaxPages}.");
            }

            pages = new List<DocumentPage>(pageCount);
            totalCharacters = 0;

            // Every page is read: a receipt may start on page 2, and a document may hold several (§24).
            for (var number = 1; number <= pageCount; number++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var text = document.GetPage(number).Text;
                totalCharacters += text?.Length ?? 0;
                pages.Add(new DocumentPage(number, text, null));
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // A PDF that will not parse is a user-facing message, never an unhandled exception (§38).
            logger.LogWarning(exception, "Failed to parse a PDF document");

            return Result.Failure<ProcessedDocument>(
                ErrorCodes.DocumentUnreadable,
                "I could not open that PDF. It may be corrupt or password-protected.");
        }

        var averageCharacters = totalCharacters / pageCount;
        var kind = averageCharacters < _options.ScannedTextThresholdPerPage
            ? DocumentKind.ScannedPdf
            : DocumentKind.TextPdf;

        var canUploadDirectly = _options.AllowDirectPdfUpload
            && content.LongLength <= _options.ProviderMaxDirectUploadBytes
            && pageCount <= _options.ProviderMaxDirectUploadPages;

        // Rasterize only when the provider cannot read the file itself and the text layer is unusable.
        if (!canUploadDirectly && kind == DocumentKind.ScannedPdf)
        {
            var rendered = renderer.RenderPages(content, pageCount, _options.RenderDpi, cancellationToken);

            if (rendered.IsFailure)
            {
                return Result.Failure<ProcessedDocument>(rendered.Error);
            }

            pages = [.. pages.Select(page => page with { RenderedPng = rendered.Value.GetValueOrDefault(page.Number) })];
        }

        return Result.Success(new ProcessedDocument(
            kind,
            MediaTypeSniffer.Pdf,
            fileName,
            content.LongLength,
            pageCount,
            content,
            pages,
            canUploadDirectly));
    }

    private static Task<Result<ProcessedDocument>> Fail(string code, string message) =>
        Task.FromResult(Result.Failure<ProcessedDocument>(code, message));
}
