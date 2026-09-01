namespace Justina.Core.Domain.Results;

/// <summary>
/// A machine-readable failure. Codes are stable contract values: the AI layer relays them,
/// so they must never carry secrets or internal detail.
/// </summary>
public sealed record Error(string Code, string Message)
{
    public static readonly Error None = new(string.Empty, string.Empty);

    public override string ToString() => $"{Code}: {Message}";
}

public static class ErrorCodes
{
    public const string Validation = "validation_failed";
    public const string NotFound = "not_found";
    public const string InvalidState = "invalid_workflow_state";
    public const string Unauthorized = "unauthorized";
    public const string Conflict = "conflict";
    public const string UnsupportedMedia = "unsupported_media";
    public const string MediaTooLarge = "media_too_large";
    public const string DocumentUnreadable = "document_unreadable";
    public const string TooManyPages = "too_many_pages";
    public const string VisionFailed = "vision_failed";
    public const string ExternalApiFailed = "external_api_failed";
    public const string NotAvailable = "not_available";
}
