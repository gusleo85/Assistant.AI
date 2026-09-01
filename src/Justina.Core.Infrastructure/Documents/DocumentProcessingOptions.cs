namespace Justina.Core.Infrastructure.Documents;

public sealed class DocumentProcessingOptions
{
    public const string SectionName = "DocumentProcessing";

    /// <summary>Hard cap on accepted media. Kept below the provider's own limit and mirrored in NGINX.</summary>
    public long MaxBytes { get; set; } = 20 * 1024 * 1024;

    public int MaxPages { get; set; } = 20;

    /// <summary>
    /// Average characters per page below which a PDF is treated as scanned and must be rasterized.
    /// </summary>
    public int ScannedTextThresholdPerPage { get; set; } = 80;

    /// <summary>Provider limits for direct PDF upload; above these, the local fallback path is used.</summary>
    public long ProviderMaxDirectUploadBytes { get; set; } = 32 * 1024 * 1024;

    public int ProviderMaxDirectUploadPages { get; set; } = 100;

    /// <summary>Whether direct PDF upload to the vision provider is enabled at all.</summary>
    public bool AllowDirectPdfUpload { get; set; } = true;

    public int RenderDpi { get; set; } = 200;

    /// <summary>How long downloaded media is retained before the cleanup pass removes it.</summary>
    public TimeSpan MediaRetention { get; set; } = TimeSpan.FromHours(6);
}
