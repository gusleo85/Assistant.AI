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

public sealed class GetReceiptQueryHandler(IReceiptAccess receipts)
    : IQueryHandler<GetReceiptQuery, ReceiptSnapshot>
{
    public async Task<Result<ReceiptSnapshot>> HandleAsync(
        GetReceiptQuery query,
        CancellationToken cancellationToken)
    {
        // Both paths go through the access guard: an explicit id from another conversation must not be
        // readable, or merchant, amount and expense reference leak across users (§34).
        var loaded = query.ReceiptId is { } explicitId
            ? await receipts.GetAsync(query.Context, explicitId, cancellationToken).ConfigureAwait(false)
            : await receipts.GetActiveAsync(query.Context, cancellationToken).ConfigureAwait(false);

        return loaded.IsFailure
            ? Result.Failure<ReceiptSnapshot>(loaded.Error)
            : Result.Success(ReceiptSnapshot.From(loaded.Value));
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

public sealed class GetReceiptStatusQueryHandler(IReceiptAccess receipts)
    : IQueryHandler<GetReceiptStatusQuery, ReceiptStatus>
{
    public async Task<Result<ReceiptStatus>> HandleAsync(
        GetReceiptStatusQuery query,
        CancellationToken cancellationToken)
    {
        var loaded = await receipts
            .GetAsync(query.Context, query.ReceiptId, cancellationToken)
            .ConfigureAwait(false);

        if (loaded.IsFailure)
        {
            return Result.Failure<ReceiptStatus>(loaded.Error);
        }

        var receipt = loaded.Value;

        return Result.Success(new ReceiptStatus(
            receipt.Id,
            receipt.State.ToString(),
            receipt.State == Domain.ReceiptState.WaitingConfirmation,
            receipt.IsTerminal,
            receipt.ExternalExpenseId,
            receipt.FailureReason));
    }
}
