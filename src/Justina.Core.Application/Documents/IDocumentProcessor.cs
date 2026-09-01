using Justina.Core.Domain.Results;

namespace Justina.Core.Application.Documents;

public enum DocumentKind
{
    Image = 0,

    /// <summary>A PDF whose pages carry a usable text layer.</summary>
    TextPdf = 1,

    /// <summary>A PDF with little or no text layer — pages must be rasterized to be read.</summary>
    ScannedPdf = 2,
}

/// <summary>
/// One page of a processed document. <see cref="RenderedPng"/> is populated only when the page had to be
/// rasterized, because rendering is expensive and unnecessary for text PDFs.
/// </summary>
public sealed record DocumentPage(int Number, string? Text, byte[]? RenderedPng);

/// <summary>
/// The normalized result of validating and inspecting untrusted user media (§24).
/// Both channels and both domains consume this same shape.
/// </summary>
public sealed record ProcessedDocument(
    DocumentKind Kind,
    string MimeType,
    string? FileName,
    long SizeBytes,
    int PageCount,
    byte[] Content,
    IReadOnlyList<DocumentPage> Pages,
    bool SupportsDirectProviderUpload);

public interface IDocumentProcessor
{
    /// <summary>
    /// Validates, inspects and normalizes untrusted media. Never throws for bad input —
    /// a malformed or oversized document is a <see cref="Result"/> failure the user can be told about.
    /// </summary>
    Task<Result<ProcessedDocument>> ProcessAsync(
        byte[] content,
        string declaredMimeType,
        string? fileName,
        CancellationToken cancellationToken);
}
