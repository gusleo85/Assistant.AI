using Justina.Expense.Domain;

namespace Justina.Expense.Application.Receipts;

public sealed record ReceiptLineItemSnapshot(string Description, decimal Quantity, decimal UnitPrice, decimal Amount);

/// <summary>
/// The structured view the agent renders to the user (§28). The agent chooses wording; it never invents
/// values, because everything it can show comes from here.
/// </summary>
public sealed record ReceiptSnapshot(
    Guid ReceiptId,
    string State,
    string? Merchant,
    DateOnly? Date,
    string? Currency,
    decimal? Amount,
    string? Category,
    string? ReceiptNumber,
    decimal? TaxAmount,
    IReadOnlyList<ReceiptLineItemSnapshot> LineItems,
    Guid? CategoryId,
    IReadOnlyList<Guid> TaxIds,
    string? Location,
    Guid? BatchId,
    int SequenceInBatch,
    bool AwaitingConfirmation,
    bool IsSubmittable,
    string? MissingField,
    string? ExternalExpenseId,
    string? FailureReason)
{
    public static ReceiptSnapshot From(Receipt receipt)
    {
        var submittable = receipt.IsSubmittable(out var missingField);

        return new ReceiptSnapshot(
            receipt.Id,
            receipt.State.ToString(),
            receipt.Merchant,
            receipt.ReceiptDate,
            receipt.Currency,
            receipt.Amount,
            receipt.Category,
            receipt.ReceiptNumber,
            receipt.TaxAmount,
            receipt.LineItems
                .Select(i => new ReceiptLineItemSnapshot(i.Description, i.Quantity, i.UnitPrice, i.Amount))
                .ToList(),
            receipt.CategoryId,
            receipt.TaxIds,
            receipt.Location,
            receipt.BatchId,
            receipt.SequenceInBatch,
            receipt.State == ReceiptState.WaitingConfirmation,
            submittable,
            missingField,
            receipt.ExternalExpenseId,
            receipt.FailureReason);
    }
}

/// <summary>
/// Returned when one document produced several receipts. The agent must ask before anything is submitted,
/// and each member is confirmed separately (§25).
/// </summary>
public sealed record ReceiptExtractionOutcome(
    int ReceiptCount,
    Guid? BatchId,
    IReadOnlyList<ReceiptSnapshot> Receipts)
{
    public bool RequiresBatchDecision => ReceiptCount > 1;
}
