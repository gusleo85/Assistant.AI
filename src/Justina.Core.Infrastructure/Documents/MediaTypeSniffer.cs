namespace Justina.Core.Infrastructure.Documents;

/// <summary>
/// Identifies media by its bytes, not by what the sender claims. A declared MIME type is attacker-supplied
/// input; the magic bytes are what the parsers will actually act on (§38).
/// </summary>
public static class MediaTypeSniffer
{
    public const string Pdf = "application/pdf";
    public const string Jpeg = "image/jpeg";
    public const string Png = "image/png";
    public const string Webp = "image/webp";

    private static readonly byte[] PdfMagic = "%PDF"u8.ToArray();
    private static readonly byte[] JpegMagic = [0xFF, 0xD8, 0xFF];
    private static readonly byte[] PngMagic = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
    private static readonly byte[] RiffMagic = "RIFF"u8.ToArray();
    private static readonly byte[] WebpMagic = "WEBP"u8.ToArray();

    /// <returns>The detected media type, or <c>null</c> when the content is not a supported format.</returns>
    public static string? Sniff(ReadOnlySpan<byte> content)
    {
        if (StartsWith(content, PdfMagic))
        {
            return Pdf;
        }

        if (StartsWith(content, JpegMagic))
        {
            return Jpeg;
        }

        if (StartsWith(content, PngMagic))
        {
            return Png;
        }

        if (StartsWith(content, RiffMagic) && content.Length >= 12 && content[8..12].SequenceEqual(WebpMagic))
        {
            return Webp;
        }

        return null;
    }

    public static bool IsImage(string mimeType) => mimeType is Jpeg or Png or Webp;

    private static bool StartsWith(ReadOnlySpan<byte> content, ReadOnlySpan<byte> magic) =>
        content.Length >= magic.Length && content[..magic.Length].SequenceEqual(magic);
}
