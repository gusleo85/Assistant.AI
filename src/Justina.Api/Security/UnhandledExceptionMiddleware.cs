using System.Text.Json;
using Justina.Core.Domain.Results;

namespace Justina.Api.Security;

/// <summary>
/// Turns an unhandled exception into the same <c>{ "ok": false, "error": { … } }</c> shape every other
/// tool response uses.
///
/// Two reasons this matters. A bare HTTP 500 reads to the agent as a transport failure rather than a
/// refusal it can relay to the person waiting. And without it, a development-mode deployment would answer
/// with a full stack trace including absolute source paths — internal detail that must never leave the
/// service (§38).
/// </summary>
public sealed class UnhandledExceptionMiddleware(RequestDelegate next, ILogger<UnhandledExceptionMiddleware> logger)
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await next(context).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            // The detail goes to the log, where it is useful; the caller gets a stable, opaque shape.
            logger.LogError(
                exception,
                "Unhandled exception while handling {Method} {Path}",
                context.Request.Method,
                context.Request.Path);

            if (context.Response.HasStarted)
            {
                // Too late to rewrite the response; letting it bubble would only obscure the log entry.
                throw;
            }

            context.Response.Clear();
            context.Response.StatusCode = StatusCodes.Status500InternalServerError;
            context.Response.ContentType = "application/json";

            var body = new
            {
                ok = false,
                error = new
                {
                    code = ErrorCodes.ExternalApiFailed,
                    message = "Something went wrong on my side. Please try again.",
                },
            };

            await context.Response
                .WriteAsync(JsonSerializer.Serialize(body, SerializerOptions))
                .ConfigureAwait(false);
        }
    }
}
