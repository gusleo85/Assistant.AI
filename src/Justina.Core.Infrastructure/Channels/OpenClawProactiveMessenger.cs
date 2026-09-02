using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Justina.Core.Application.Abstractions;
using Justina.Core.Domain.Messaging;
using Justina.Core.Domain.Results;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Justina.Core.Infrastructure.Channels;

/// <summary>
/// Where the OpenClaw gateway is, and the token it demands.
///
/// Traffic between the two has been one-way until now — the gateway calls Justina over MCP — so the app
/// held no gateway credentials at all. Sending a message the user did not ask for is the first thing
/// that needs them.
/// </summary>
public sealed class OpenClawGatewayOptions
{
    public const string SectionName = "OpenClawGateway";

    /// <summary>For example http://justina-openclaw:18789. Empty disables proactive messaging.</summary>
    public string BaseUrl { get; set; } = string.Empty;

    /// <summary>The gateway's own token. A non-loopback bind makes gateway auth mandatory.</summary>
    public string Token { get; set; } = string.Empty;

    public int TimeoutSeconds { get; set; } = 30;

    public bool IsConfigured => !string.IsNullOrWhiteSpace(BaseUrl) && !string.IsNullOrWhiteSpace(Token);
}

/// <summary>
/// Sends a message through the OpenClaw gateway's tool-invoke endpoint.
///
/// <code>
/// POST {gateway}/tools/invoke
/// { "tool": "message", "action": "json",
///   "args": { "action": "send", "channel": "telegram", "to": "…", "text": "…" } }
/// </code>
///
/// The shape was established against the running gateway rather than from documentation, which does not
/// name the tool. Two things learned there are worth keeping in mind: an unknown tool answers
/// <c>not_found</c> while a known one with wrong arguments answers <c>tool_error</c>, and the endpoint's
/// <c>dryRun</c> flag is <b>not</b> honoured by this tool — a "dry run" delivers a real message to a real
/// person. There is therefore no safe way to rehearse this call against a live recipient.
/// </summary>
public sealed class OpenClawProactiveMessenger(
    HttpClient httpClient,
    IOptions<OpenClawGatewayOptions> options,
    ILogger<OpenClawProactiveMessenger> logger)
    : IProactiveMessenger
{
    private readonly OpenClawGatewayOptions _options = options.Value;

    public async Task<Result> SendAsync(
        ChannelRecipient recipient,
        string message,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(recipient);

        if (string.IsNullOrWhiteSpace(message))
        {
            // Nothing to say is not an error, but sending an empty message would leave the recipient
            // staring at a blank notification from a bot.
            return Result.Failure(ErrorCodes.Validation, "There was nothing to send.");
        }

        if (!_options.IsConfigured)
        {
            logger.LogError("No OpenClaw gateway is configured, so nothing can be sent to {Channel}", recipient.Channel);

            return Result.Failure(ErrorCodes.NotAvailable, "I cannot send messages right now.");
        }

        var payload = new JsonObject
        {
            ["tool"] = "message",
            ["action"] = "json",
            ["args"] = new JsonObject
            {
                ["action"] = "send",
                ["channel"] = recipient.Channel.ToString().ToLowerInvariant(),
                ["to"] = recipient.UserId,
                ["text"] = message,
            },
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, "tools/invoke")
        {
            Content = JsonContent.Create(payload),
        };

        request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {_options.Token}");

        try
        {
            using var response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode || !Accepted(body))
            {
                // The body is logged truncated: it carries the gateway's own diagnostics, not ours, and
                // never reaches the user.
                logger.LogError(
                    "The gateway refused to message {Channel} user {UserId}: {StatusCode} {Body}",
                    recipient.Channel,
                    recipient.UserId,
                    (int)response.StatusCode,
                    body.Length > 300 ? body[..300] : body);

                return Result.Failure(ErrorCodes.ExternalApiFailed, "I could not send that message.");
            }

            logger.LogInformation(
                "Sent a proactive message to {Channel} user {UserId}",
                recipient.Channel,
                recipient.UserId);

            return Result.Success();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            logger.LogError(
                exception,
                "Could not reach the gateway to message {Channel} user {UserId}",
                recipient.Channel,
                recipient.UserId);

            return Result.Failure(ErrorCodes.ExternalApiFailed, "I could not send that message.");
        }
    }

    /// <summary>
    /// The gateway answers 200 for a refused tool as well as a delivered message — the failure is inside
    /// the body as <c>ok: false</c>. Reading only the status code would report every refusal as a
    /// success, and the caller would tell someone their message had been sent.
    /// </summary>
    private static bool Accepted(string body)
    {
        try
        {
            return JsonNode.Parse(body)?["ok"]?.GetValue<bool>() == true;
        }
        catch (Exception exception) when (exception is System.Text.Json.JsonException or InvalidOperationException or FormatException)
        {
            return false;
        }
    }
}
