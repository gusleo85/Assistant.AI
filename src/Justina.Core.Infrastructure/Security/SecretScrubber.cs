using System.Text.RegularExpressions;

namespace Justina.Core.Infrastructure.Security;

/// <summary>
/// Removes credentials from anything that might be recorded.
///
/// This exists because Telegram puts the bot token in the URL **path**
/// (<c>https://api.telegram.org/bot&lt;token&gt;/getFile</c>). Default <c>HttpClient</c> logging and
/// HTTP tracing both record request URLs, so without scrubbing the token would end up in log lines and
/// span attributes — the exact thing §40 forbids.
/// </summary>
public static partial class SecretScrubber
{
    private const string Redacted = "***";

    /// <summary>Query-string parameters whose values are never safe to record.</summary>
    private static readonly string[] SensitiveQueryKeys =
    [
        "access_token", "token", "api_key", "apikey", "key", "secret", "password", "signature",
    ];

    [GeneratedRegex(@"/bot[^/]+", RegexOptions.IgnoreCase)]
    private static partial Regex TelegramBotPath();

    [GeneratedRegex(@"(?<key>[?&](?:access_token|token|api_key|apikey|key|secret|password|signature)=)[^&]*",
        RegexOptions.IgnoreCase)]
    private static partial Regex SensitiveQueryValue();

    public static string Redact(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var scrubbed = TelegramBotPath().Replace(value, $"/bot{Redacted}");
        return SensitiveQueryValue().Replace(scrubbed, $"${{key}}{Redacted}");
    }

    public static string Redact(Uri? uri) => uri is null ? string.Empty : Redact(uri.ToString());

    /// <summary>True when a header must never have its value recorded.</summary>
    public static bool IsSensitiveHeader(string headerName) =>
        headerName.Equals("Authorization", StringComparison.OrdinalIgnoreCase)
        || headerName.Equals("Proxy-Authorization", StringComparison.OrdinalIgnoreCase)
        || headerName.Equals("X-Justina-Tool-Key", StringComparison.OrdinalIgnoreCase)
        || headerName.Equals("X-Hub-Signature-256", StringComparison.OrdinalIgnoreCase)
        || headerName.Contains("api-key", StringComparison.OrdinalIgnoreCase)
        || headerName.Contains("token", StringComparison.OrdinalIgnoreCase);

    /// <summary>The query keys this scrubber knows about, exposed so tests and callers stay in step.</summary>
    public static IReadOnlyList<string> KnownSensitiveQueryKeys => SensitiveQueryKeys;
}
