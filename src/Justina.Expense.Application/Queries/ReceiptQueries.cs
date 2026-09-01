using Justina.Core.Application.Abstractions;
using Justina.Core.Application.Messaging;
using Justina.Core.Domain.Identity;
using Justina.Core.Domain.Results;
using Justina.Expense.Application.Abstractions;
using Justina.Expense.Application.Receipts;

namespace Justina.Expense.Application.Queries;

/// <summary>
/// Reads the receipt the agent should display. Omitting the id means "the one this conversation is
/// working on", so the agent never has to remember identifiers between turns.
/// </summary>
public sealed record GetReceiptQuery(RequestContext Context, Guid? ReceiptId)
    : IQuery<ReceiptSnapshot>, IRequireCapability
{
    public string RequiredCapability => Capabilities.ExpenseRead;
}

public sealed class GetReceiptQueryHandler(
    IReceiptRepository receipts,
    IConversationStateStore conversations)
    : IQueryHandler<GetReceiptQuery, ReceiptSnapshot>
{
    public async Task<Result<ReceiptSnapshot>> HandleAsync(
        GetReceiptQuery query,
        CancellationToken cancellationToken)
    {
        if (query.ReceiptId is { } explicitId)
        {
            var byId = await receipts.GetAsync(explicitId, cancellationToken).ConfigureAwait(false);

            return byId is null
                ? Result.Failure<ReceiptSnapshot>(ErrorCodes.NotFound, "That receipt no longer exists.")
                : Result.Success(ReceiptSnapshot.From(byId));
        }

        var conversation = await conversations
            .GetAsync(query.Context.Channel, query.Context.ConversationId, cancellationToken)
            .ConfigureAwait(false);

        if (conversation is null)
        {
            return Result.Failure<ReceiptSnapshot>(ErrorCodes.NotFound, "There is no receipt in progress.");
        }

        var active = await receipts
            .GetActiveForConversationAsync(conversation.Id, cancellationToken)
            .ConfigureAwait(false);

        return active is null
            ? Result.Failure<ReceiptSnapshot>(ErrorCodes.NotFound, "There is no receipt in progress.")
            : Result.Success(ReceiptSnapshot.From(active));
    }
}

public sealed record ReceiptStatus(
    Guid ReceiptId,
    string State,
    bool AwaitingConfirmation,
    bool IsTerminal,
    string? ExternalExpenseId,
    string? FailureReason);

public sealed record GetReceiptStatusQuery(RequestContext Context, Guid ReceiptId)
    : IQuery<ReceiptStatus>, IRequireCapability
{
    public string RequiredCapability => Capabilities.ExpenseRead;
}

public sealed class GetReceiptStatusQueryHandler(IReceiptRepository receipts)
    : IQueryHandler<GetReceiptStatusQuery, ReceiptStatus>
{
    public async Task<Result<ReceiptStatus>> HandleAsync(
        GetReceiptStatusQuery query,
        CancellationToken cancellationToken)
    {
        var receipt = await receipts.GetAsync(query.ReceiptId, cancellationToken).ConfigureAwait(false);

        return receipt is null
            ? Result.Failure<ReceiptStatus>(ErrorCodes.NotFound, "That receipt no longer exists.")
            : Result.Success(new ReceiptStatus(
                receipt.Id,
                receipt.State.ToString(),
                receipt.State == Domain.ReceiptState.WaitingConfirmation,
                receipt.IsTerminal,
                receipt.ExternalExpenseId,
                receipt.FailureReason));
    }
}
