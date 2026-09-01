namespace Justina.Expense.Domain;

/// <summary>
/// The authoritative receipt lifecycle (§30). C# owns this: the LLM may interpret intent, but it can only
/// ask for a transition — it can never assert one.
/// </summary>
public enum ReceiptState
{
    Received = 0,
    Extracting = 1,
    ExtractionFailed = 2,
    WaitingConfirmation = 3,
    Confirmed = 4,
    Submitting = 5,
    Submitted = 6,
    SubmissionFailed = 7,
    Cancelled = 8,
}

public enum ReceiptField
{
    Merchant = 0,
    Date = 1,
    Currency = 2,
    Amount = 3,
    Category = 4,
    ReceiptNumber = 5,
    TaxAmount = 6,
    Location = 7,
}

/// <summary>
/// One requested field change. Edits are expressed as an explicit list so "only the requested fields
/// change" is a structural guarantee rather than a convention (§29).
/// </summary>
public sealed record ReceiptFieldChange
{
    public required ReceiptField Field { get; init; }

    public string? StringValue { get; init; }

    public decimal? DecimalValue { get; init; }

    public DateOnly? DateValue { get; init; }
}

/// <summary>The set of values Vision extracts, after C# validation and normalization.</summary>
/// <summary>
/// <paramref name="CategoryId"/> and <paramref name="TaxIds"/> are resolved in C# against the company's
/// catalogue before they reach the domain — a model answers with names, never with identifiers. They
/// default to null so every existing caller keeps compiling unchanged.
/// </summary>
public sealed record ReceiptFields(
    string? Merchant,
    DateOnly? Date,
    string? Currency,
    decimal? Amount,
    string? Category,
    string? ReceiptNumber,
    decimal? TaxAmount,
    Guid? CategoryId = null,
    IReadOnlyList<Guid>? TaxIds = null,
    string? Location = null);
