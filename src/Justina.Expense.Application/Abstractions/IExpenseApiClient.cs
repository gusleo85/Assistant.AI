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
    CorrelationId CorrelationId,
    Guid? CategoryId = null,
    IReadOnlyList<Guid>? TaxIds = null,
    string? Location = null,
    Guid? CurrencyId = null,

    /// <summary>
    /// Which company and member this expense belongs to, resolved from the channel identity before the
    /// call is built. The API needs it on every request; the caller never states it.
    /// </summary>
    ExpenseTenant? Tenant = null,

    /// <summary>
    /// The stored image this expense came from. The Expense API creates its receipt from the photo
    /// itself, so the bytes have to travel with the submission — not just the values read out of them.
    /// </summary>
    string? SourceMediaId = null,

    /// <summary>
    /// The receipt the Expense API already created for this submission, if a previous attempt got that
    /// far. Supplied so a retry writes its values onto that receipt instead of creating a second one.
    /// </summary>
    string? ExternalReceiptId = null,

    /// <summary>
    /// Called the moment the Expense API creates its receipt, before the values are written.
    ///
    /// It exists because the interesting failure is between the two calls: an expense exists, its values
    /// do not, and nothing has returned yet. Telling the caller at that point is what lets a retry
    /// resume — a delegate rather than a return value precisely because there may be no return.
    /// </summary>
    Action<string>? OnReceiptCreated = null)
{
    /// <summary>The catalogue taxes matched on this receipt. Never null, so callers need no guard.</summary>
    public IReadOnlyList<Guid> TaxIds { get; init; } = TaxIds ?? [];
}

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
