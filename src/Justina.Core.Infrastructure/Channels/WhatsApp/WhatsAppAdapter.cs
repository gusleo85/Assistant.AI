using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Justina.Core.Application.Channels;
using Justina.Core.Domain.Messaging;
using Justina.Core.Domain.Results;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Justina.Core.Infrastructure.Channels.WhatsApp;

public sealed class WhatsAppOptions
{
    public const string SectionName = "WhatsApp";

    /// <summary>From configuration only; sent as a bearer token and never logged (§40).</summary>
    public string AccessToken { get; set; } = string.Empty;

    public string PhoneNumberId { get; set; } = string.Empty;

    public string GraphBaseUrl { get; set; } = "https://graph.facebook.com/v21.0";

    /// <summary>Used to verify the X-Hub-Signature-256 header on inbound webhooks.</summary>
    public string AppSecret { get; set; } = string.Empty;

    /// <summary>Echoed back during webhook subscription setup.</summary>
    public string WebhookVerifyToken { get; set; } = string.Empty;

    public int TimeoutSeconds { get; set; } = 30;
}

/// <summary>
/// WhatsApp Cloud API media fetch: resolve the media id to a short-lived URL, then download it with the
/// same bearer token. Produces the same <see cref="DownloadedMedia"/> as Telegram, which is what lets one
/// document pipeline serve both channels (§22).
/// </summary>
public sealed class WhatsAppMediaDownloader(
    HttpClient httpClient,
    IOptions<WhatsAppOptions> options,
    ILogger<WhatsAppMediaDownloader> logger)
    : IChannelMediaDownloader
{
    private readonly WhatsAppOptions _options = options.Value;

    public ChannelKind Channel => ChannelKind.WhatsApp;

    public async Task<Result<DownloadedMedia>> DownloadAsync(
        MediaReference media,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(media);

        if (string.IsNullOrWhiteSpace(_options.AccessToken))
        {
            logger.LogError("WhatsApp is not configured: no access token supplied");
            return Result.Failure<DownloadedMedia>(ErrorCodes.NotAvailable, "WhatsApp is not available right now.");
        }

        try
        {
            using var lookup = await httpClient
                .GetAsync(media.MediaId, cancellationToken)
                .ConfigureAwait(false);

            if (!lookup.IsSuccessStatusCode)
            {
                logger.LogWarning("WhatsApp media lookup failed with {StatusCode}", (int)lookup.StatusCode);
                return Result.Failure<DownloadedMedia>(
                    ErrorCodes.NotFound,
                    "That WhatsApp file is no longer available.");
            }

            var body = await lookup.Content.ReadFromJsonAsync<JsonNode>(cancellationToken).ConfigureAwait(false);
            var url = body?["url"]?.GetValue<string>();
            var mimeType = body?["mime_type"]?.GetValue<string>() ?? media.MimeType;

            if (string.IsNullOrWhiteSpace(url))
            {
                return Result.Failure<DownloadedMedia>(
                    ErrorCodes.NotFound,
                    "That WhatsApp file is no longer available.");
            }

            // The media URL is on a different host and needs the bearer token attached explicitly.
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.AccessToken);

            using var download = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);

            if (!download.IsSuccessStatusCode)
            {
                logger.LogWarning("WhatsApp media download failed with {StatusCode}", (int)download.StatusCode);
                return Result.Failure<DownloadedMedia>(
                    ErrorCodes.DocumentUnreadable,
                    "I could not download that file from WhatsApp.");
            }

            var content = await download.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);

            return Result.Success(new DownloadedMedia(content, mimeType, media.FileName));
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            logger.LogWarning(exception, "WhatsApp media download failed");
            return Result.Failure<DownloadedMedia>(
                ErrorCodes.DocumentUnreadable,
                "I could not download that file from WhatsApp.");
        }
    }
}

public sealed class WhatsAppResponder(
    HttpClient httpClient,
    IOptions<WhatsAppOptions> options,
    ILogger<WhatsAppResponder> logger)
    : IChannelResponder
{
    private readonly WhatsAppOptions _options = options.Value;

    public ChannelKind Channel => ChannelKind.WhatsApp;

    public async Task<Result> SendTextAsync(string conversationId, string text, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_options.AccessToken) || string.IsNullOrWhiteSpace(_options.PhoneNumberId))
        {
            return Result.Failure(ErrorCodes.NotAvailable, "WhatsApp is not available right now.");
        }

        var payload = new JsonObject
        {
            ["messaging_product"] = "whatsapp",
            ["recipient_type"] = "individual",
            ["to"] = conversationId,
            ["type"] = "text",
            ["text"] = new JsonObject { ["body"] = text },
        };

        try
        {
            using var response = await httpClient
                .PostAsJsonAsync($"{_options.PhoneNumberId}/messages", payload, cancellationToken)
                .ConfigureAwait(false);

            if (response.IsSuccessStatusCode)
            {
                return Result.Success();
            }

            logger.LogWarning("WhatsApp send failed with {StatusCode}", (int)response.StatusCode);
            return Result.Failure(ErrorCodes.ExternalApiFailed, "I could not send that message to WhatsApp.");
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            logger.LogWarning(exception, "WhatsApp send failed");
            return Result.Failure(ErrorCodes.ExternalApiFailed, "I could not send that message to WhatsApp.");
        }
    }
}
