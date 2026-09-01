using Justina.Core.Application.Abstractions;
using Justina.Core.Application.Messaging;
using Justina.Core.Domain;
using Justina.Core.Domain.Identity;
using Justina.Core.Domain.Results;
using Justina.Expense.Application.Abstractions;
using Justina.Expense.Application.Receipts;
using Justina.Expense.Domain;

namespace Justina.Expense.Application.Commands;

/// <summary>
/// Applies natural-language edits the agent has already turned into field/value pairs.
/// Validation happens in C#; the aggregate re-asserts its own invariants (§29).
/// </summary>
public sealed record UpdateReceiptCommand(
    RequestContext Context,
    Guid ReceiptId,
    IReadOnlyCollection<ReceiptEditRequest> Edits) : ICommand<ReceiptSnapshot>, IRequireCapability
{
    public string RequiredCapability => Capabilities.ExpenseSubmit;
}

public sealed class UpdateReceiptCommandHandler(
    IReceiptRepository receipts,
    IUnitOfWork unitOfWork,
    IClock clock)
    : ICommandHandler<UpdateReceiptCommand, ReceiptSnapshot>
{
    public async Task<Result<ReceiptSnapshot>> HandleAsync(
        UpdateReceiptCommand command,
        CancellationToken cancellationToken)
    {
        var receipt = await receipts.GetAsync(command.ReceiptId, cancellationToken).ConfigureAwait(false);

        if (receipt is null)
        {
            return Result.Failure<ReceiptSnapshot>(ErrorCodes.NotFound, "That receipt no longer exists.");
        }

        if (receipt.State != ReceiptState.WaitingConfirmation)
        {
            return Result.Failure<ReceiptSnapshot>(
                ErrorCodes.InvalidState,
                $"This receipt is {receipt.State} and can no longer be edited.");
        }

        var translated = ReceiptEditTranslator.Translate(command.Edits);

        if (translated.IsFailure)
        {
            return Result.Failure<ReceiptSnapshot>(translated.Error);
        }

        try
        {
            receipt.ApplyChanges(translated.Value, command.Context.User.UserId, clock.UtcNow);
        }
        catch (DomainException exception)
        {
            return Result.Failure<ReceiptSnapshot>(ErrorCodes.Validation, exception.Message);
        }

        var saved = await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return saved.IsFailure
            ? Result.Failure<ReceiptSnapshot>(saved.Error)
            : Result.Success(ReceiptSnapshot.From(receipt));
    }
}

/// <summary>
/// The user's explicit go-ahead, and the only path to the Expense API (§28, §31).
/// Idempotent by receipt id: confirming twice returns the first outcome.
/// </summary>
public sealed record ConfirmReceiptCommand(RequestContext Context, Guid ReceiptId)
    : ICommand<ReceiptSnapshot>, IRequireCapability, IIdempotentCommand
{
    public string RequiredCapability => Capabilities.ExpenseSubmit;

    public string IdempotencyKey => $"confirm:{ReceiptId}";
}

public sealed class ConfirmReceiptCommandHandler(
    IReceiptRepository receipts,
    IReceiptSubmissionService submission,
    IConversationStateStore conversations,
    IUnitOfWork unitOfWork,
    IClock clock)
    : ICommandHandler<ConfirmReceiptCommand, ReceiptSnapshot>
{
    public async Task<Result<ReceiptSnapshot>> HandleAsync(
        ConfirmReceiptCommand command,
        CancellationToken cancellationToken)
    {
        var receipt = await receipts.GetAsync(command.ReceiptId, cancellationToken).ConfigureAwait(false);

        if (receipt is null)
        {
            return Result.Failure<ReceiptSnapshot>(ErrorCodes.NotFound, "That receipt no longer exists.");
        }

        // A second confirmation of an already-submitted receipt returns the existing expense (§33).
        if (receipt.State == ReceiptState.Submitted)
        {
            return Result.Success(ReceiptSnapshot.From(receipt));
        }

        if (receipt.State != ReceiptState.WaitingConfirmation)
        {
            return Result.Failure<ReceiptSnapshot>(
                ErrorCodes.InvalidState,
                $"This receipt is {receipt.State} and is not awaiting confirmation.");
        }

        if (!receipt.IsSubmittable(out var missingField))
        {
            return Result.Failure<ReceiptSnapshot>(
                ErrorCodes.Validation,
                $"This receipt is missing {missingField}. Please provide it before confirming.");
        }

        receipt.Confirm(command.Context.User.UserId, clock.UtcNow);

        var saved = await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        if (saved.IsFailure)
        {
            return Result.Failure<ReceiptSnapshot>(saved.Error);
        }

        var result = await submission
            .SubmitAsync(receipt, command.Context.User.UserId, command.Context.CorrelationId, cancellationToken)
            .ConfigureAwait(false);

        if (result.IsSuccess)
        {
            await ClearWorkflowIfDoneAsync(command, receipt, cancellationToken).ConfigureAwait(false);
        }

        return result;
    }

    private async Task ClearWorkflowIfDoneAsync(
        ConfirmReceiptCommand command,
        Receipt receipt,
        CancellationToken cancellationToken)
    {
        var siblings = receipt.BatchId is { } batchId
            ? await receipts.GetByBatchAsync(batchId, cancellationToken).ConfigureAwait(false)
            : [receipt];

        // A batch keeps the workflow open until every receipt in it has been dealt with (§25).
        if (siblings.All(r => r.IsTerminal))
        {
            await conversations
                .SetActiveWorkflowAsync(receipt.ConversationId, null, null, cancellationToken)
                .ConfigureAwait(false);

            await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
    }
}

/// <summary>Cancelling must never submit anything (§54 rule 5).</summary>
public sealed record CancelReceiptCommand(RequestContext Context, Guid ReceiptId)
    : ICommand<ReceiptSnapshot>, IRequireCapability
{
    public string RequiredCapability => Capabilities.ExpenseSubmit;
}

public sealed class CancelReceiptCommandHandler(
    IReceiptRepository receipts,
    IConversationStateStore conversations,
    IUnitOfWork unitOfWork,
    IClock clock)
    : ICommandHandler<CancelReceiptCommand, ReceiptSnapshot>
{
    public async Task<Result<ReceiptSnapshot>> HandleAsync(
        CancelReceiptCommand command,
        CancellationToken cancellationToken)
    {
        var receipt = await receipts.GetAsync(command.ReceiptId, cancellationToken).ConfigureAwait(false);

        if (receipt is null)
        {
            return Result.Failure<ReceiptSnapshot>(ErrorCodes.NotFound, "That receipt no longer exists.");
        }

        if (receipt.State == ReceiptState.Cancelled)
        {
            return Result.Success(ReceiptSnapshot.From(receipt));
        }

        if (receipt.IsTerminal)
        {
            return Result.Failure<ReceiptSnapshot>(
                ErrorCodes.InvalidState,
                "This receipt has already been submitted and cannot be cancelled.");
        }

        receipt.Cancel(command.Context.User.UserId, clock.UtcNow);

        await conversations
            .SetActiveWorkflowAsync(receipt.ConversationId, null, null, cancellationToken)
            .ConfigureAwait(false);

        var saved = await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return saved.IsFailure
            ? Result.Failure<ReceiptSnapshot>(saved.Error)
            : Result.Success(ReceiptSnapshot.From(receipt));
    }
}

/// <summary>
/// Retries a submission that failed after confirmation. Confirmation is not asked for again — the user
/// already gave it, and the idempotency key is unchanged, so this cannot create a second expense.
/// </summary>
public sealed record SubmitExpenseCommand(RequestContext Context, Guid ReceiptId)
    : ICommand<ReceiptSnapshot>, IRequireCapability
{
    public string RequiredCapability => Capabilities.ExpenseSubmit;
}

public sealed class SubmitExpenseCommandHandler(
    IReceiptRepository receipts,
    IReceiptSubmissionService submission)
    : ICommandHandler<SubmitExpenseCommand, ReceiptSnapshot>
{
    public async Task<Result<ReceiptSnapshot>> HandleAsync(
        SubmitExpenseCommand command,
        CancellationToken cancellationToken)
    {
        var receipt = await receipts.GetAsync(command.ReceiptId, cancellationToken).ConfigureAwait(false);

        if (receipt is null)
        {
            return Result.Failure<ReceiptSnapshot>(ErrorCodes.NotFound, "That receipt no longer exists.");
        }

        if (receipt.State is not (ReceiptState.Confirmed or ReceiptState.SubmissionFailed or ReceiptState.Submitted))
        {
            return Result.Failure<ReceiptSnapshot>(
                ErrorCodes.InvalidState,
                "This receipt has not been confirmed, so it cannot be submitted.");
        }

        return await submission
            .SubmitAsync(receipt, command.Context.User.UserId, command.Context.CorrelationId, cancellationToken)
            .ConfigureAwait(false);
    }
}
