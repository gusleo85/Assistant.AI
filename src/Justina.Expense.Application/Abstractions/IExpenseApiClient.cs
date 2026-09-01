using Justina.Core.Domain.Observability;
using Justina.Core.Domain.Results;

namespace Justina.Expense.Application.Abstractions;

public sealed record ExpenseLineItem(string Description, decimal Quantity, decimal UnitPrice, decimal Amount);

/// <summary>
/// The payload Justina submits. This is Justina's own contract; mapping it onto the external Expense
/// API's wire format happens in Infrastructure, so a contract change touches one class.
/// </summary>
public sealed record ExpenseSubmission(
    string Merchant,
    DateOnly Date,
    string Currency,
    decimal Amount,
    string? Category,
    string? ReceiptNumber,
    decimal? TaxAmount,
    IReadOnlyList<ExpenseLineItem> LineItems,
    string SubmittedByUserId,
    string IdempotencyKey,
    CorrelationId CorrelationId);

public sealed record ExpenseSubmissionResult(string ExternalExpenseId);

/// <summary>
/// The only route to the external Expense API (§31). Agents never construct HTTP calls; they call a tool
/// that ends up here after validation, authorization and state checks.
/// </summary>
public interface IExpenseApiClient
{
    Task<Result<ExpenseSubmissionResult>> SubmitAsync(
        ExpenseSubmission submission,
        CancellationToken cancellationToken);
}
