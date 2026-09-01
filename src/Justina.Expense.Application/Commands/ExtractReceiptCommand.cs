using System.Text.Json;
using Justina.Core.Application.Abstractions;
using Justina.Core.Application.Documents;
using Justina.Core.Application.Messaging;
using Justina.Core.Application.Vision;
using Justina.Core.Domain.Identity;
using Justina.Core.Domain.Results;
using Justina.Expense.Application.Abstractions;
using Justina.Expense.Application.Receipts;
using Justina.Expense.Domain;
using Microsoft.Extensions.Logging;

namespace Justina.Expense.Application.Commands;

/// <summary>
/// Reads the stored document with Vision and produces one or more validated receipts.
/// Separate from <see cref="ReceiveReceiptCommand"/> so a Vision failure can be retried without
/// re-downloading from the channel.
/// </summary>
public sealed record ExtractReceiptCommand(RequestContext Context, Guid ReceiptId)
    : ICommand<ReceiptExtractionOutcome>, IRequireCapability
{
    public string RequiredCapability => Capabilities.ExpenseSubmit;
}

public sealed class ExtractReceiptCommandHandler(
    IReceiptRepository receipts,
    IMediaStore mediaStore,
    IDocumentProcessor documentProcessor,
    IVisionProvider vision,
    IUnitOfWork unitOfWork,
    IClock clock,
    ILogger<ExtractReceiptCommandHandler> logger)
    : ICommandHandler<ExtractReceiptCommand, ReceiptExtractionOutcome>
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<Result<ReceiptExtractionOutcome>> HandleAsync(
        ExtractReceiptCommand command,
        CancellationToken cancellationToken)
    {
        var receipt = await receipts.GetAsync(command.ReceiptId, cancellationToken).ConfigureAwait(false);

        if (receipt is null)
        {
            return Result.Failure<ReceiptExtractionOutcome>(ErrorCodes.NotFound, "That receipt no longer exists.");
        }

        if (receipt.State != ReceiptState.Received)
        {
            return Result.Failure<ReceiptExtractionOutcome>(
                ErrorCodes.InvalidState,
                $"This receipt is already {receipt.State} and cannot be extracted again.");
        }

        var now = clock.UtcNow;
        receipt.BeginExtraction(now);

        var document = await LoadDocumentAsync(receipt.SourceMediaId, cancellationToken).ConfigureAwait(false);

        if (document.IsFailure)
        {
            return await FailAsync(receipt, document.Error, cancellationToken).ConfigureAwait(false);
        }

        var extraction = await vision
            .ExtractAsync(
                new VisionRequest(
                    document.Value,
                    ReceiptExtractionSchema.Name,
                    ReceiptExtractionSchema.Json,
                    ReceiptExtractionSchema.Instruction),
                cancellationToken)
            .ConfigureAwait(false);

        if (extraction.IsFailure)
        {
            return await FailAsync(receipt, extraction.Error, cancellationToken).ConfigureAwait(false);
        }

        var candidates = Parse(extraction.Value.Json);

        if (candidates.Count == 0)
        {
            return await FailAsync(
                    receipt,
                    new Error(ErrorCodes.VisionFailed, "No receipt could be read from that document."),
                    cancellationToken)
                .ConfigureAwait(false);
        }

        var snapshots = Materialize(receipt, candidates, now);

        var saved = await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        if (saved.IsFailure)
        {
            return Result.Failure<ReceiptExtractionOutcome>(saved.Error);
        }

        logger.LogInformation(
            "Extracted {ReceiptCount} receipt(s) from media of receipt {ReceiptId} using {Model}",
            snapshots.Count,
            receipt.Id,
            extraction.Value.Model);

        return Result.Success(new ReceiptExtractionOutcome(snapshots.Count, receipt.BatchId, snapshots));
    }

    private async Task<Result<ProcessedDocument>> LoadDocumentAsync(
        string mediaId,
        CancellationToken cancellationToken)
    {
        var media = await mediaStore.GetAsync(mediaId, cancellationToken).ConfigureAwait(false);

        if (media.IsFailure)
        {
            return Result.Failure<ProcessedDocument>(media.Error);
        }

        return await documentProcessor
            .ProcessAsync(media.Value.Content, media.Value.MimeType, media.Value.FileName, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Turns Vision candidates into receipts. Several candidates become several receipts sharing a batch —
    /// they are never merged into one expense (§25).
    /// </summary>
    private List<ReceiptSnapshot> Materialize(Receipt receipt, IReadOnlyList<RawReceipt> candidates, DateTimeOffset now)
    {
        var snapshots = new List<ReceiptSnapshot>(candidates.Count);

        if (candidates.Count > 1)
        {
            var batch = ReceiptBatch.Create(receipt.ConversationId, receipt.SourceMediaId, now);
            receipts.AddBatch(batch);
            receipt.AttachToBatch(batch.Id, now);

            Complete(receipt, candidates[0], now);
            snapshots.Add(ReceiptSnapshot.From(receipt));

            foreach (var candidate in candidates.Skip(1))
            {
                var sibling = Receipt.Create(receipt.ConversationId, receipt.SourceMediaId, batch.Id, now);
                sibling.BeginExtraction(now);
                Complete(sibling, candidate, now);
                receipts.Add(sibling);
                snapshots.Add(ReceiptSnapshot.From(sibling));
            }

            return snapshots;
        }

        Complete(receipt, candidates[0], now);
        snapshots.Add(ReceiptSnapshot.From(receipt));
        return snapshots;
    }

    private static void Complete(Receipt receipt, RawReceipt candidate, DateTimeOffset now)
    {
        var normalized = ReceiptNormalizer.Normalize(candidate);
        receipt.CompleteExtraction(normalized.Fields, normalized.LineItems, now);
    }

    private static IReadOnlyList<RawReceipt> Parse(string json)
    {
        try
        {
            var extraction = JsonSerializer.Deserialize<RawExtraction>(json, JsonOptions);
            return extraction?.Receipts ?? [];
        }
        catch (JsonException)
        {
            // A provider that answers off-schema is a failed extraction, not a crash.
            return [];
        }
    }

    private async Task<Result<ReceiptExtractionOutcome>> FailAsync(
        Receipt receipt,
        Error error,
        CancellationToken cancellationToken)
    {
        receipt.FailExtraction(error.Code, clock.UtcNow);
        await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        logger.LogWarning("Extraction failed for receipt {ReceiptId}: {ErrorCode}", receipt.Id, error.Code);
        return Result.Failure<ReceiptExtractionOutcome>(error);
    }
}
