namespace Justina.Core.Infrastructure.Vision;

public sealed class OpenAiVisionOptions
{
    public const string SectionName = "OpenAiVision";

    /// <summary>Supplied by configuration only. Never logged, never sent to the agent layer (§38).</summary>
    public string ApiKey { get; set; } = string.Empty;

    public string BaseUrl { get; set; } = "https://api.openai.com/v1";

    public string Model { get; set; } = "gpt-4.1";

    public int TimeoutSeconds { get; set; } = 120;

    /// <summary>Cap on rasterized pages sent in the fallback path, to bound cost and latency.</summary>
    public int MaxRenderedPages { get; set; } = 10;

    /// <summary>Cap on characters of extracted PDF text sent in the text fallback path.</summary>
    public int MaxTextCharacters { get; set; } = 60_000;
}
