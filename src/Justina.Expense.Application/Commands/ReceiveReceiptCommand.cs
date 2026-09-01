using Justina.Core.Application.Abstractions;
using Justina.Core.Application.Channels;
using Justina.Core.Application.Documents;
using Justina.Core.Application.Messaging;
using Justina.Core.Domain.Identity;
using Justina.Core.Domain.Messaging;
using Justina.Core.Domain.Results;
using Justina.Expense.Application.Abstractions;
using Justina.Expense.Domain;
using Microsoft.Extensions.Logging;

namespace Justina.Expense.Application.Commands;

public sealed record ReceiveReceiptResult(Guid ReceiptId, string DocumentKind, int PageCount);

/// <summary>
/// Registers inbound media as a receipt in <see cref="ReceiptState.Received"/>.
/// Downloading and validating happens here; reading the content does not — extraction is a separate,
/// independently retryable command.
/// </summary>
public sealed record ReceiveReceiptCommand(
    RequestContext Context,
    MediaReference Media,
    string MessageId) : ICommand<ReceiveReceiptResult>, IRequireCapability, IIdempotentCommand
{
    public string RequiredCapability => Capabilities.ExpenseSubmit;

    // One inbound message may only ever produce one receipt, however often the channel retries it.
    public string IdempotencyKey => $"receive:{Context.Channel}:{MessageId}";
}

public sealed class ReceiveReceiptCommandHandler(
    IChannelRegistry channels,
    IDocumentProcessor documentProcessor,
    IMediaStore mediaStore,
    IReceiptRepository receipts,
    IConversationStateStore conversations,
    IUnitOfWork unitOfWork,
    IClock clock,
    ILogger<ReceiveReceiptCommandHandler> logger)
    : ICommandHandler<ReceiveReceiptCommand, ReceiveReceiptResult>
{
    public async Task<Result<ReceiveReceiptResult>> HandleAsync(
        ReceiveReceiptCommand command,
        CancellationToken cancellationToken)
    {
        var downloader = channels.GetDownloader(command.Context.Channel);

        if (downloader.IsFailure)
        {
            return Result.Failure<ReceiveReceiptResult>(downloader.Error);
        }

        var download = await downloader.Value
            .DownloadAsync(command.Media, cancellationToken)
            .ConfigureAwait(false);

        if (download.IsFailure)
        {
            return Result.Failure<ReceiveReceiptResult>(download.Error);
        }

        // Validate before anything is stored: untrusted media must never be persisted unchecked (§38).
        var processed = await documentProcessor
            .ProcessAsync(
                download.Value.Content,
                download.Value.MimeType,
                download.Value.FileName,
                cancellationToken)
            .ConfigureAwait(false);

        if (processed.IsFailure)
        {
            return Result.Failure<ReceiveReceiptResult>(processed.Error);
        }

        var stored = await mediaStore
            .SaveAsync(
                new StoredMedia(
                    command.Media.MediaId,
                    download.Value.Content,
                    download.Value.MimeType,
                    download.Value.FileName),
                cancellationToken)
            .ConfigureAwait(false);

        if (stored.IsFailure)
        {
            return Result.Failure<ReceiveReceiptResult>(stored.Error);
        }

        var conversation = await conversations
            .EnsureAsync(
                command.Context.Channel,
                command.Context.ConversationId,
                command.Context.User.UserId,
                cancellationToken)
            .ConfigureAwait(false);

        var now = clock.UtcNow;
        var receipt = Receipt.Create(conversation.Id, command.Media.MediaId, batchId: null, now);
        receipts.Add(receipt);

        await conversations
            .SetActiveWorkflowAsync(conversation.Id, Workflows.ExpenseReceipt, receipt.Id, cancellationToken)
            .ConfigureAwait(false);

        var saved = await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        if (saved.IsFailure)
        {
            return Result.Failure<ReceiveReceiptResult>(saved.Error);
        }

        logger.LogInformation(
            "Receipt {ReceiptId} received from {DocumentKind} with {PageCount} page(s)",
            receipt.Id,
            processed.Value.Kind,
            processed.Value.PageCount);

        return Result.Success(new ReceiveReceiptResult(
            receipt.Id,
            processed.Value.Kind.ToString(),
            processed.Value.PageCount));
    }
}
