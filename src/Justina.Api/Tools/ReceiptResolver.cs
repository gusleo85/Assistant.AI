using Justina.Core.Application.Abstractions;
using Justina.Core.Application.Messaging;
using Justina.Core.Domain.Results;
using Justina.Expense.Application.Abstractions;
using Justina.Expense.Application.Receipts;

namespace Justina.Api.Tools;

/// <summary>
/// Lets the agent say "this receipt" without tracking identifiers between turns: when no id is supplied,
/// the conversation's active receipt is used.
///
/// This is a convenience, <b>not</b> an authorization control. An explicitly supplied id is passed
/// through unchecked here; ownership is enforced in the Application layer by
/// <c>IReceiptAccess</c>, which every handler loads through.
/// </summary>
public interface IReceiptResolver
{
    Task<Result<Guid>> ResolveAsync(
        RequestContext context,
        Guid? explicitReceiptId,
        CancellationToken cancellationToken);

    Task<Result<ReceiptExtractionOutcome>> GetActiveOutcomeAsync(
        RequestContext context,
        CancellationToken cancellationToken);
}

public sealed class ReceiptResolver(
    IConversationStateStore conversations,
    IReceiptRepository receipts)
    : IReceiptResolver
{
    public async Task<Result<Guid>> ResolveAsync(
        RequestContext context,
        Guid? explicitReceiptId,
        CancellationToken cancellationToken)
    {
        if (explicitReceiptId is { } id)
        {
            return Result.Success(id);
        }

        var active = await FindActiveAsync(context, cancellationToken).ConfigureAwait(false);

        return active is null
            ? Result.Failure<Guid>(ErrorCodes.NotFound, "There is no receipt in progress in this conversation.")
            : Result.Success(active.Id);
    }

    public async Task<Result<ReceiptExtractionOutcome>> GetActiveOutcomeAsync(
        RequestContext context,
        CancellationToken cancellationToken)
    {
        var active = await FindActiveAsync(context, cancellationToken).ConfigureAwait(false);

        if (active is null)
        {
            return Result.Failure<ReceiptExtractionOutcome>(
                ErrorCodes.NotFound,
                "There is no receipt in progress in this conversation.");
        }

        var siblings = active.BatchId is { } batchId
            ? await receipts.GetByBatchAsync(batchId, cancellationToken).ConfigureAwait(false)
            : [active];

        var snapshots = siblings.Select(ReceiptSnapshot.From).ToList();

        return Result.Success(new ReceiptExtractionOutcome(snapshots.Count, active.BatchId, snapshots));
    }

    private async Task<Justina.Expense.Domain.Receipt?> FindActiveAsync(
        RequestContext context,
        CancellationToken cancellationToken)
    {
        var conversation = await conversations
            .GetAsync(context.Channel, context.ConversationId, cancellationToken)
            .ConfigureAwait(false);

        return conversation is null
            ? null
            : await receipts
                .GetActiveForConversationAsync(conversation.Id, cancellationToken)
                .ConfigureAwait(false);
    }
}
