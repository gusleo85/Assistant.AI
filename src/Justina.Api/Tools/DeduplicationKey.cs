using System.Security.Cryptography;
using System.Text;

namespace Justina.Api.Tools;

/// <summary>
/// The identity of an inbound document, for deduplication.
///
/// The staged path wins over the message id, because the file is what must not be processed twice: the
/// gateway replays earlier images into later turns, so one photo can arrive under several message ids.
///
/// The key is bounded, and that is not a detail. A staged path runs to about 131 characters against a
/// 128-character column, so using it raw truncated on insert — and because the deduplicator read any
/// save failure as "already seen", the receipt was silently never created and the user was told nothing
/// was in progress. Hashing removes the length question entirely.
/// </summary>
public static class DeduplicationKey
{
    /// <summary>Comfortably inside the column, and long enough that a collision is not a real concern.</summary>
    private const int HexLength = 32;

    /// <returns>
    /// A stable key for whichever identifier the caller actually has. A short message id is passed
    /// through so the table stays readable; anything path-shaped is hashed.
    /// </returns>
    public static string For(string? stagedPath, string? messageId, string? mediaId)
    {
        if (!string.IsNullOrWhiteSpace(stagedPath))
        {
            return "file:" + Hash(stagedPath);
        }

        var fallback = new[] { messageId, mediaId }
            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));

        if (string.IsNullOrWhiteSpace(fallback))
        {
            // Callers validate before reaching here; this keeps the method total rather than throwing.
            return "unknown:" + Guid.NewGuid().ToString("N");
        }

        return fallback.Length <= 64 ? fallback : "id:" + Hash(fallback);
    }

    private static string Hash(string value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..HexLength];
}
