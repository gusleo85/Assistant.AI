using System.Globalization;
using System.Text;

namespace Justina.Core.UnitTests;

/// <summary>
/// Builds small, structurally valid PDFs so the document pipeline can be tested without binary fixtures
/// checked into the repository. Offsets and the xref table are computed, because a PDF with a broken xref
/// exercises the lenient-parsing path rather than the one we want to assert on.
/// </summary>
public static class TestPdf
{
    /// <summary>A PDF with a real text layer on every page — the "text PDF" branch.</summary>
    public static byte[] WithText(params string[] pageTexts) => Build(pageTexts);

    /// <summary>A PDF whose pages carry no text — the "scanned PDF" branch.</summary>
    public static byte[] WithoutText(int pageCount) =>
        Build(Enumerable.Repeat(string.Empty, pageCount).ToArray());

    private static byte[] Build(IReadOnlyList<string> pageTexts)
    {
        var pageCount = pageTexts.Count;
        var output = new MemoryStream();
        var offsets = new List<long>();

        void Write(string text) => output.Write(Encoding.ASCII.GetBytes(text));

        void BeginObject(int number)
        {
            offsets.Add(output.Position);
            Write($"{number} 0 obj\n");
        }

        Write("%PDF-1.4\n");

        // 1: catalog, 2: page tree, 3..: one page and one content stream each, then the font.
        var firstPageObject = 3;
        var fontObject = firstPageObject + (pageCount * 2);

        BeginObject(1);
        Write("<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");

        var kids = string.Join(' ', Enumerable.Range(0, pageCount).Select(i => $"{firstPageObject + (i * 2)} 0 R"));
        BeginObject(2);
        Write($"<< /Type /Pages /Kids [{kids}] /Count {pageCount} >>\nendobj\n");

        for (var index = 0; index < pageCount; index++)
        {
            var pageObject = firstPageObject + (index * 2);
            var contentObject = pageObject + 1;

            BeginObject(pageObject);
            Write(
                $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] " +
                $"/Resources << /Font << /F1 {fontObject} 0 R >> >> /Contents {contentObject} 0 R >>\nendobj\n");

            var text = pageTexts[index];
            var stream = string.IsNullOrEmpty(text)
                ? "q Q\n"
                : $"BT /F1 12 Tf 40 800 Td ({Escape(text)}) Tj ET\n";

            BeginObject(contentObject);
            Write($"<< /Length {stream.Length} >>\nstream\n{stream}endstream\nendobj\n");
        }

        BeginObject(fontObject);
        Write("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>\nendobj\n");

        var xrefPosition = output.Position;
        var objectCount = fontObject + 1;

        Write($"xref\n0 {objectCount}\n");
        Write("0000000000 65535 f \n");

        foreach (var offset in offsets)
        {
            Write($"{offset.ToString("D10", CultureInfo.InvariantCulture)} 00000 n \n");
        }

        Write($"trailer\n<< /Size {objectCount} /Root 1 0 R >>\nstartxref\n{xrefPosition}\n%%EOF\n");

        return output.ToArray();
    }

    private static string Escape(string text) =>
        text.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("(", "\\(", StringComparison.Ordinal)
            .Replace(")", "\\)", StringComparison.Ordinal);
}
