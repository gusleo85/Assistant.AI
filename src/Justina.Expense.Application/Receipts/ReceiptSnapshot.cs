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

    /// <summary>
    /// What each matched tax is called in the company's catalogue — "GST9 (9.00%)" — for the agent to
    /// show beside the amount. An amount on its own hides which rate was matched, and a tax filed at the
    /// wrong rate looks entirely normal afterwards; the name is what lets the user catch it.
    /// </summary>
    IReadOnlyList<string> Taxes,
    string? Location,
    Guid? CurrencyId,
    Guid? BatchId,
    int SequenceInBatch,
    bool AwaitingConfirmation,
    bool IsSubmittable,
    string? MissingField,

    /// <summary>
    /// A category was read from the receipt but matched nothing in the company catalogue. The name is
    /// still shown, because it is what the receipt says — but it will not file against anything, so the
    /// user has to pick a real one.
    /// </summary>
    bool CategoryUnresolved,

    /// <summary>The currency code did not match a catalogue currency. Same reasoning.</summary>
    bool CurrencyUnresolved,

    /// <summary>
    /// The receipt printed a tax amount but nothing matched the company's predefined taxes. Worth raising
    /// rather than filing as no tax at all — the amount is evidence a tax exists.
    /// </summary>
    bool TaxUnresolved,
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
            receipt.TaxLabels,
            receipt.Location,
            receipt.CurrencyId,
            receipt.BatchId,
            receipt.SequenceInBatch,
            receipt.State == ReceiptState.WaitingConfirmation,
            submittable,
            missingField,
            // A name with no id resolved against nothing in the catalogue.
            receipt.Category is not null && receipt.CategoryId is null,
            receipt.Currency is not null && receipt.CurrencyId is null,
            receipt.TaxAmount is not null && receipt.TaxIds.Count == 0,
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
