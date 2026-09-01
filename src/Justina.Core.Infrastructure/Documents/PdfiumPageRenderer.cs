using Justina.Core.Domain.Results;
using Microsoft.Extensions.Logging;

namespace Justina.Core.Infrastructure.Documents;

/// <summary>
/// Rasterizes PDF pages for the fallback path. Behind an interface so the document pipeline can be unit
/// tested without a native renderer.
/// </summary>
public interface IPdfPageRenderer
{
    Result<IReadOnlyDictionary<int, byte[]>> RenderPages(
        byte[] pdf,
        int pageCount,
        int dpi,
        CancellationToken cancellationToken);
}

/// <summary>
/// PDFium via PDFtoImage. Chosen over shelling out to Ghostscript or ImageMagick, which are large native
/// attack surfaces for untrusted input (§38).
/// </summary>
public sealed class PdfiumPageRenderer(ILogger<PdfiumPageRenderer> logger) : IPdfPageRenderer
{
    public Result<IReadOnlyDictionary<int, byte[]>> RenderPages(
        byte[] pdf,
        int pageCount,
        int dpi,
        CancellationToken cancellationToken)
    {
        var rendered = new Dictionary<int, byte[]>(pageCount);

        try
        {
            for (var number = 1; number <= pageCount; number++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                using var output = new MemoryStream();

                // CA1416: PDFium supports every platform Justina runs on — Linux in the container,
                // Windows and macOS for development. The unsupported targets (browser/WASM) are not
                // deployment targets for this service.
#pragma warning disable CA1416
                PDFtoImage.Conversion.SavePng(
                    output,
                    pdf,
                    page: number - 1,
                    options: new PDFtoImage.RenderOptions(Dpi: dpi));
#pragma warning restore CA1416

                rendered[number] = output.ToArray();
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "Failed to rasterize a PDF page");

            return Result.Failure<IReadOnlyDictionary<int, byte[]>>(
                ErrorCodes.DocumentUnreadable,
                "I could not read the pages of that PDF.");
        }

        return Result.Success<IReadOnlyDictionary<int, byte[]>>(rendered);
    }
}
