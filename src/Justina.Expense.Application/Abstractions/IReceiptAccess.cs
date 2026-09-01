using Justina.Core.Application.Abstractions;
using Justina.Core.Application.Messaging;
using Justina.Core.Domain.Results;
using Justina.Expense.Domain;

namespace Justina.Expense.Application.Abstractions;

/// <summary>
/// The single way a handler obtains a receipt.
///
/// A capability answers "may this principal submit expenses at all" — it does not answer "may this
/// principal touch <em>this</em> receipt". Loading by id alone would let any caller act on a receipt
/// belonging to someone else's conversation, so every load goes through here and is checked against the
/// caller's conversation (§34).
/// </summary>
public interface IReceiptAccess
{
    /// <summary>
    /// Loads a receipt the caller is entitled to. A receipt belonging to another conversation is reported
    /// as <see cref="ErrorCodes.NotFound"/>, not <see cref="ErrorCodes.Unauthorized"/>, so an id cannot be
    /// probed for existence.
    /// </summary>
    Task<Result<Receipt>> GetAsync(RequestContext context, Guid receiptId, CancellationToken cancellationToken);

    /// <summary>The receipt this conversation is currently working on, if any.</summary>
    Task<Result<Receipt>> GetActiveAsync(RequestContext context, CancellationToken cancellationToken);

    /// <summary>
    /// An explicitly identified receipt, or the conversation's active one when no id is supplied.
    ///
    /// Resolution deliberately lives here rather than at the API edge: it runs inside the handler, which
    /// means it is behind the authorization decorator. Resolving first would let an unmapped caller learn
    /// whether a conversation has a receipt in progress before being refused.
    /// </summary>
    Task<Result<Receipt>> ResolveAsync(
        RequestContext context,
        Guid? receiptId,
        CancellationToken cancellationToken);
}

public sealed class ReceiptAccess(
    IConversationStateStore conversations,
    IReceiptRepository receipts)
    : IReceiptAccess
{
    public async Task<Result<Receipt>> GetAsync(
        RequestContext context,
        Guid receiptId,
        CancellationToken cancellationToken)
    {
        var receipt = await receipts.GetAsync(receiptId, cancellationToken).ConfigureAwait(false);

        if (receipt is null)
        {
            return NotFound();
        }

        var conversation = await conversations
            .GetAsync(context.Channel, context.ConversationId, cancellationToken)
            .ConfigureAwait(false);

        // No conversation, or a different one: the caller has no claim on this receipt.
        if (conversation is null || receipt.ConversationId != conversation.Id)
        {
            return NotFound();
        }

        return Result.Success(receipt);
    }

    public Task<Result<Receipt>> ResolveAsync(
        RequestContext context,
        Guid? receiptId,
        CancellationToken cancellationToken) =>
        receiptId is { } id
            ? GetAsync(context, id, cancellationToken)
            : GetActiveAsync(context, cancellationToken);

    public async Task<Result<Receipt>> GetActiveAsync(RequestContext context, CancellationToken cancellationToken)
    {
        var conversation = await conversations
            .GetAsync(context.Channel, context.ConversationId, cancellationToken)
            .ConfigureAwait(false);

        if (conversation is null)
        {
            return NoneInProgress();
        }

        var receipt = await receipts
            .GetActiveForConversationAsync(conversation.Id, cancellationToken)
            .ConfigureAwait(false);

        return receipt is null ? NoneInProgress() : Result.Success(receipt);
    }

    private static Result<Receipt> NotFound() =>
        Result.Failure<Receipt>(ErrorCodes.NotFound, "That receipt no longer exists.");

    private static Result<Receipt> NoneInProgress() =>
        Result.Failure<Receipt>(ErrorCodes.NotFound, "There is no receipt in progress in this conversation.");
}
