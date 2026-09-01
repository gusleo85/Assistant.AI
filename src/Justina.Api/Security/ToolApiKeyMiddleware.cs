using System.Security.Cryptography;
using System.Text;

namespace Justina.Api.Security;

public sealed class ToolApiOptions
{
    public const string SectionName = "ToolApi";

    /// <summary>Shared with OpenClaw over the Docker network. Never exposed publicly by NGINX (§19).</summary>
    public string SharedSecret { get; set; } = string.Empty;

    public string HeaderName { get; set; } = "X-Justina-Tool-Key";
}

/// <summary>
/// Guards the tool surface. The comparison is fixed-time so a wrong key cannot be discovered by timing,
/// and a missing configured secret fails closed rather than open.
/// </summary>
public sealed class ToolApiKeyMiddleware(RequestDelegate next, ToolApiOptions options, ILogger<ToolApiKeyMiddleware> logger)
{
    private readonly byte[] _expected = Encoding.UTF8.GetBytes(options.SharedSecret);

    public async Task InvokeAsync(HttpContext context)
    {
        if (!context.Request.Path.StartsWithSegments("/tools", StringComparison.OrdinalIgnoreCase))
        {
            await next(context).ConfigureAwait(false);
            return;
        }

        if (_expected.Length == 0)
        {
            logger.LogError("The tool API shared secret is not configured; refusing every tool call");
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            return;
        }

        var supplied = context.Request.Headers[options.HeaderName].ToString();

        if (!IsValid(supplied))
        {
            logger.LogWarning("Rejected a tool call with a missing or invalid key");
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        await next(context).ConfigureAwait(false);
    }

    private bool IsValid(string supplied)
    {
        if (string.IsNullOrEmpty(supplied))
        {
            return false;
        }

        var candidate = Encoding.UTF8.GetBytes(supplied);

        return CryptographicOperations.FixedTimeEquals(candidate, _expected);
    }
}
