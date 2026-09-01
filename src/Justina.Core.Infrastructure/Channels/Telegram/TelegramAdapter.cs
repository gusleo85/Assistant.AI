using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Justina.Core.Application.Channels;
using Justina.Core.Domain.Messaging;
using Justina.Core.Domain.Results;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Justina.Core.Infrastructure.Channels.Telegram;

public sealed class TelegramOptions
{
    public const string SectionName = "Telegram";

    /// <summary>From configuration only. It appears in request URLs, so it is never logged (§40).</summary>
    public string BotToken { get; set; } = string.Empty;

    public string ApiBaseUrl { get; set; } = "https://api.telegram.org";

    public int TimeoutSeconds { get; set; } = 30;
}

/// <summary>
/// Telegram's two-step media fetch: resolve the file path, then download it. Everything channel-specific
/// stops here — callers get bytes and a MIME type (§35).
/// </summary>
public sealed class TelegramMediaDownloader(
    HttpClient httpClient,
    IOptions<TelegramOptions> options,
    ILogger<TelegramMediaDownloader> logger)
    : IChannelMediaDownloader
{
    private readonly TelegramOptions _options = options.Value;

    public ChannelKind Channel => ChannelKind.Telegram;

    public async Task<Result<DownloadedMedia>> DownloadAsync(
        MediaReference media,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(media);

        if (string.IsNullOrWhiteSpace(_options.BotToken))
        {
            logger.LogError("Telegram is not configured: no bot token supplied");
            return Result.Failure<DownloadedMedia>(ErrorCodes.NotAvailable, "Telegram is not available right now.");
        }

        try
        {
            var filePath = await ResolveFilePathAsync(media.MediaId, cancellationToken).ConfigureAwait(false);

            if (filePath.IsFailure)
            {
                return Result.Failure<DownloadedMedia>(filePath.Error);
            }

            using var response = await httpClient
                .GetAsync($"file/bot{_options.BotToken}/{filePath.Value}", cancellationToken)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("Telegram file download failed with {StatusCode}", (int)response.StatusCode);
                return Result.Failure<DownloadedMedia>(
                    ErrorCodes.DocumentUnreadable,
                    "I could not download that file from Telegram.");
            }

            var content = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);

            return Result.Success(new DownloadedMedia(content, media.MimeType, media.FileName));
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            logger.LogWarning(exception, "Telegram media download failed");
            return Result.Failure<DownloadedMedia>(
                ErrorCodes.DocumentUnreadable,
                "I could not download that file from Telegram.");
        }
    }

    private async Task<Result<string>> ResolveFilePathAsync(string fileId, CancellationToken cancellationToken)
    {
        using var response = await httpClient
            .GetAsync($"bot{_options.BotToken}/getFile?file_id={Uri.EscapeDataString(fileId)}", cancellationToken)
            .ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            logger.LogWarning("Telegram getFile failed with {StatusCode}", (int)response.StatusCode);
            return Result.Failure<string>(ErrorCodes.NotFound, "That Telegram file is no longer available.");
        }

        var body = await response.Content.ReadFromJsonAsync<JsonNode>(cancellationToken).ConfigureAwait(false);
        var filePath = body?["result"]?["file_path"]?.GetValue<string>();

        return string.IsNullOrWhiteSpace(filePath)
            ? Result.Failure<string>(ErrorCodes.NotFound, "That Telegram file is no longer available.")
            : Result.Success(filePath);
    }
}

public sealed class TelegramResponder(
    HttpClient httpClient,
    IOptions<TelegramOptions> options,
    ILogger<TelegramResponder> logger)
    : IChannelResponder
{
    private readonly TelegramOptions _options = options.Value;

    public ChannelKind Channel => ChannelKind.Telegram;

    public async Task<Result> SendTextAsync(string conversationId, string text, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_options.BotToken))
        {
            return Result.Failure(ErrorCodes.NotAvailable, "Telegram is not available right now.");
        }

        var payload = new JsonObject
        {
            ["chat_id"] = conversationId,
            ["text"] = text,
        };

        try
        {
            using var response = await httpClient
                .PostAsJsonAsync($"bot{_options.BotToken}/sendMessage", payload, cancellationToken)
                .ConfigureAwait(false);

            if (response.IsSuccessStatusCode)
            {
                return Result.Success();
            }

            logger.LogWarning("Telegram sendMessage failed with {StatusCode}", (int)response.StatusCode);
            return Result.Failure(ErrorCodes.ExternalApiFailed, "I could not send that message to Telegram.");
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            logger.LogWarning(exception, "Telegram sendMessage failed");
            return Result.Failure(ErrorCodes.ExternalApiFailed, "I could not send that message to Telegram.");
        }
    }
}
