using System.Text.Json;
using Justina.Core.Domain;

namespace Justina.Expense.Domain;

/// <summary>
/// Raised when a caller asks for a transition the lifecycle forbids. Callers are expected to check state
/// first (the tool layer turns this into a typed refusal); reaching this exception means a defect.
/// </summary>
public sealed class ReceiptStateException(ReceiptState from, string action)
    : DomainException($"A receipt in state '{from}' cannot perform '{action}'.")
{
    public ReceiptState From { get; } = from;

    public string Action { get; } = action;
}

/// <summary>
/// The Expense aggregate root and the authoritative owner of receipt state (§30).
/// Every mutation is a method here, every method records an audit event, and illegal transitions throw.
/// </summary>
public sealed class Receipt
{
    private readonly List<ReceiptLineItem> _lineItems = [];
    private readonly List<ReceiptEvent> _events = [];
    private readonly List<Guid> _taxIds = [];
    private readonly List<string> _taxLabels = [];

    // EF Core materialization.
    private Receipt()
    {
        SourceMediaId = string.Empty;
    }

    private Receipt(Guid conversationId, string sourceMediaId, Guid? batchId, int sequenceInBatch, DateTimeOffset now)
    {
        Id = Guid.NewGuid();
        ConversationId = conversationId;
        SourceMediaId = sourceMediaId;
        BatchId = batchId;
        SequenceInBatch = sequenceInBatch;
        State = ReceiptState.Received;
        CreatedAtUtc = now;
        UpdatedAtUtc = now;
    }

    public Guid Id { get; private set; }

    public Guid ConversationId { get; private set; }

    public ReceiptState State { get; private set; }

    /// <summary>SQL Server <c>rowversion</c>. Two concurrent confirmations cannot both win (§22).</summary>
    public byte[]? RowVersion { get; private set; }

    public string SourceMediaId { get; private set; }

    /// <summary>Set when the source document held several receipts (§25).</summary>
    public Guid? BatchId { get; private set; }

    /// <summary>
    /// Position within the source document, 1-based. Receipts in a batch are created in the same instant,
    /// so this — not the timestamp — is what makes "the next receipt to confirm" deterministic.
    /// </summary>
    public int SequenceInBatch { get; private set; } = 1;

    public string? Merchant { get; private set; }

    public DateOnly? ReceiptDate { get; private set; }

    public string? Currency { get; private set; }

    /// <summary>
    /// The Expense API's identifier for <see cref="Currency"/>, resolved from the company's currency
    /// list. Null when the code is not one the company claims in — the code is still kept, so the user
    /// can be told which currency was read rather than being shown nothing.
    /// </summary>
    public Guid? CurrencyId { get; private set; }

    public decimal? Amount { get; private set; }

    public string? Category { get; private set; }

    /// <summary>
    /// The Expense API's identifier for <see cref="Category"/>, resolved from the catalogue. Null when
    /// the category name matched nothing — the name is still kept, because a name we cannot resolve is
    /// better information for the user than no category at all.
    /// </summary>
    public Guid? CategoryId { get; private set; }

    public string? ReceiptNumber { get; private set; }

    public decimal? TaxAmount { get; private set; }

    /// <summary>Predefined taxes matched against the company's catalogue. Empty when none matched.</summary>
    public IReadOnlyList<Guid> TaxIds => _taxIds;

    /// <summary>
    /// The catalogue labels for <see cref="TaxIds"/>, positionally aligned, so the tax can be named to
    /// the user. Empty when nothing matched, and never filled from the receipt's own wording.
    /// </summary>
    public IReadOnlyList<string> TaxLabels => _taxLabels;

    /// <summary>Where the receipt was issued, as printed. Carried through to the expense record.</summary>
    public string? Location { get; private set; }

    /// <summary>The external system's expense id. Its presence is what makes a re-submit a no-op.</summary>
    public string? ExternalExpenseId { get; private set; }

    public string? FailureReason { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; private set; }

    public DateTimeOffset UpdatedAtUtc { get; private set; }

    public IReadOnlyList<ReceiptLineItem> LineItems => _lineItems;

    public IReadOnlyList<ReceiptEvent> Events => _events;

    public bool IsTerminal => State is ReceiptState.Submitted or ReceiptState.Cancelled;

    public static Receipt Create(
        Guid conversationId,
        string sourceMediaId,
        Guid? batchId,
        DateTimeOffset now,
        int sequenceInBatch = 1)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceMediaId);
        ArgumentOutOfRangeException.ThrowIfLessThan(sequenceInBatch, 1);

        var receipt = new Receipt(conversationId, sourceMediaId, batchId, sequenceInBatch, now);
        receipt.Record("Created", ReceiptState.Received, ReceiptState.Received, "system", null, now);
        return receipt;
    }

    /// <summary>
    /// Legal from <see cref="ReceiptState.ExtractionFailed"/> as well as <see cref="ReceiptState.Received"/>,
    /// so a Vision failure can be retried against the already-downloaded document.
    /// </summary>
    public void BeginExtraction(DateTimeOffset now)
    {
        if (State is not (ReceiptState.Received or ReceiptState.ExtractionFailed))
        {
            throw new ReceiptStateException(State, nameof(BeginExtraction));
        }

        FailureReason = null;
        Transition(ReceiptState.Extracting, "ExtractionStarted", "system", null, now);
    }

    /// <summary>
    /// Joins this receipt to a batch. Only meaningful while extraction is running, because a document is
    /// not known to hold several receipts until Vision has read it (§25).
    /// </summary>
    public void AttachToBatch(Guid batchId, int sequenceInBatch, DateTimeOffset now)
    {
        Require(ReceiptState.Extracting, nameof(AttachToBatch));
        ArgumentOutOfRangeException.ThrowIfLessThan(sequenceInBatch, 1);

        BatchId = batchId;
        SequenceInBatch = sequenceInBatch;
        UpdatedAtUtc = now;
    }

    public void CompleteExtraction(
        ReceiptFields fields,
        IEnumerable<ReceiptLineItem> lineItems,
        DateTimeOffset now)
    {
        Require(ReceiptState.Extracting, nameof(CompleteExtraction));

        ApplyFields(fields);
        _lineItems.Clear();
        _lineItems.AddRange(lineItems);

        Transition(ReceiptState.WaitingConfirmation, "ExtractionCompleted", "system", null, now);
    }

    public void FailExtraction(string reason, DateTimeOffset now)
    {
        Require(ReceiptState.Extracting, nameof(FailExtraction));
        FailureReason = reason;
        Transition(ReceiptState.ExtractionFailed, "ExtractionFailed", "system", Payload(new { reason }), now);
    }

    /// <summary>
    /// Applies only the requested fields and returns to <see cref="ReceiptState.WaitingConfirmation"/>,
    /// which is what forces the agent to re-display and re-ask for confirmation (§29).
    /// </summary>
    public void ApplyChanges(IReadOnlyCollection<ReceiptFieldChange> changes, string actor, DateTimeOffset now)
    {
        Require(ReceiptState.WaitingConfirmation, nameof(ApplyChanges));

        if (changes.Count == 0)
        {
            throw new DomainException("An edit must change at least one field.");
        }

        foreach (var change in changes)
        {
            Apply(change);
        }

        UpdatedAtUtc = now;
        Record(
            "Edited",
            ReceiptState.WaitingConfirmation,
            ReceiptState.WaitingConfirmation,
            actor,
            Payload(changes.Select(c => c.Field.ToString()).ToArray()),
            now);
    }

    public void Confirm(string actor, DateTimeOffset now)
    {
        Require(ReceiptState.WaitingConfirmation, nameof(Confirm));
        Transition(ReceiptState.Confirmed, "Confirmed", actor, null, now);
    }

    public void Cancel(string actor, DateTimeOffset now)
    {
        if (State is not (ReceiptState.Received
            or ReceiptState.Extracting
            or ReceiptState.ExtractionFailed
            or ReceiptState.WaitingConfirmation
            or ReceiptState.Confirmed))
        {
            throw new ReceiptStateException(State, nameof(Cancel));
        }

        Transition(ReceiptState.Cancelled, "Cancelled", actor, null, now);
    }

    public void BeginSubmission(DateTimeOffset now)
    {
        if (State is not (ReceiptState.Confirmed or ReceiptState.SubmissionFailed))
        {
            throw new ReceiptStateException(State, nameof(BeginSubmission));
        }

        Transition(ReceiptState.Submitting, "SubmissionStarted", "system", null, now);
    }

    public void CompleteSubmission(string externalExpenseId, DateTimeOffset now)
    {
        Require(ReceiptState.Submitting, nameof(CompleteSubmission));
        ArgumentException.ThrowIfNullOrWhiteSpace(externalExpenseId);

        ExternalExpenseId = externalExpenseId;
        FailureReason = null;
        Transition(ReceiptState.Submitted, "Submitted", "system", Payload(new { externalExpenseId }), now);
    }

    public void FailSubmission(string reason, DateTimeOffset now)
    {
        Require(ReceiptState.Submitting, nameof(FailSubmission));
        FailureReason = reason;
        Transition(ReceiptState.SubmissionFailed, "SubmissionFailed", "system", Payload(new { reason }), now);
    }

    /// <summary>
    /// True when every field the Expense API requires is present and well-formed.
    /// The reported name is the one a person would use — it is relayed to the user by the agent, so it
    /// must not leak a property name.
    /// </summary>
    public bool IsSubmittable(out string? missingField)
    {
        if (string.IsNullOrWhiteSpace(Merchant))
        {
            missingField = "merchant";
            return false;
        }

        if (ReceiptDate is null)
        {
            missingField = "date";
            return false;
        }

        if (!Money.IsValidCurrency(Currency))
        {
            missingField = "currency";
            return false;
        }

        if (Amount is null or <= 0m)
        {
            missingField = "amount";
            return false;
        }

        missingField = null;
        return true;
    }

    private void ApplyFields(ReceiptFields fields)
    {
        Merchant = fields.Merchant;
        ReceiptDate = fields.Date;
        Currency = fields.Currency?.ToUpperInvariant();
        CurrencyId = fields.CurrencyId;
        Amount = fields.Amount is { } amount ? decimal.Round(amount, 2, MidpointRounding.ToEven) : null;
        Category = fields.Category;
        CategoryId = fields.CategoryId;
        ReceiptNumber = fields.ReceiptNumber;
        TaxAmount = fields.TaxAmount is { } tax ? decimal.Round(tax, 2, MidpointRounding.ToEven) : null;
        Location = fields.Location;

        _taxIds.Clear();
        _taxLabels.Clear();

        if (fields.TaxIds is { Count: > 0 })
        {
            // Ids and labels are paired before the duplicates are dropped, so removing an id removes its
            // label with it. Distinct() over the two lists separately would silently shift every label
            // after the first duplicate onto the wrong tax.
            var labels = fields.TaxLabels;
            var paired = fields.TaxIds
                .Select((id, index) => (Id: id, Label: labels is not null && index < labels.Count ? labels[index] : null))
                .DistinctBy(pair => pair.Id)
                .ToList();

            _taxIds.AddRange(paired.Select(pair => pair.Id));

            // All or nothing: a partial set would leave some taxes named and others not, and the user
            // cannot tell an unnamed tax from one whose name simply did not resolve.
            if (paired.All(pair => !string.IsNullOrWhiteSpace(pair.Label)))
            {
                _taxLabels.AddRange(paired.Select(pair => pair.Label!));
            }
        }
    }

    /// <summary>
    /// Re-attaches a catalogue identifier to a category name that was edited by hand. Passing null is
    /// how a name that matched nothing is recorded — it must never keep the previous category's id.
    /// </summary>
    public void ResolveCategory(Guid? categoryId) => CategoryId = categoryId;

    /// <summary>Re-attaches a currency identifier after the code was edited by hand.</summary>
    public void ResolveCurrency(Guid? currencyId) => CurrencyId = currencyId;

    private void Apply(ReceiptFieldChange change)
    {
        switch (change.Field)
        {
            case ReceiptField.Merchant:
                Merchant = RequireString(change, ReceiptField.Merchant);
                break;

            case ReceiptField.Date:
                ReceiptDate = change.DateValue
                    ?? throw new DomainException("A date edit must supply a date.");
                break;

            case ReceiptField.Currency:
                var currency = RequireString(change, ReceiptField.Currency).ToUpperInvariant();

                if (!Money.IsValidCurrency(currency))
                {
                    throw new DomainException($"'{currency}' is not a valid ISO-4217 currency code.");
                }

                Currency = currency;

                // Same rule as the category: the new code has not been checked against the company's
                // currency list, and keeping the old id would name a different currency than the code.
                CurrencyId = null;
                break;

            case ReceiptField.Amount:
                Amount = RequirePositiveDecimal(change, ReceiptField.Amount);
                break;

            case ReceiptField.Category:
                Category = RequireString(change, ReceiptField.Category);

                // The new name has not been checked against the catalogue yet, and keeping the old id
                // would leave a receipt whose name and id name two different categories. The caller
                // re-resolves through ResolveCategory.
                CategoryId = null;
                break;

            case ReceiptField.Location:
                Location = RequireString(change, ReceiptField.Location);
                break;

            case ReceiptField.ReceiptNumber:
                ReceiptNumber = RequireString(change, ReceiptField.ReceiptNumber);
                break;

            case ReceiptField.TaxAmount:
                var tax = change.DecimalValue
                    ?? throw new DomainException("A tax edit must supply an amount.");

                if (tax < 0m)
                {
                    throw new DomainException("Tax cannot be negative.");
                }

                TaxAmount = decimal.Round(tax, 2, MidpointRounding.ToEven);
                break;

            default:
                throw new DomainException($"Unsupported receipt field '{change.Field}'.");
        }
    }

    private static string RequireString(ReceiptFieldChange change, ReceiptField field) =>
        string.IsNullOrWhiteSpace(change.StringValue)
            ? throw new DomainException($"An edit to {field} must supply a value.")
            : change.StringValue.Trim();

    private static decimal RequirePositiveDecimal(ReceiptFieldChange change, ReceiptField field)
    {
        var value = change.DecimalValue
            ?? throw new DomainException($"An edit to {field} must supply a number.");

        if (value <= 0m)
        {
            throw new DomainException($"{field} must be greater than zero.");
        }

        return decimal.Round(value, 2, MidpointRounding.ToEven);
    }

    private void Require(ReceiptState expected, string action)
    {
        if (State != expected)
        {
            throw new ReceiptStateException(State, action);
        }
    }

    private void Transition(
        ReceiptState to,
        string eventType,
        string actor,
        string? payloadJson,
        DateTimeOffset now)
    {
        var from = State;
        State = to;
        UpdatedAtUtc = now;
        Record(eventType, from, to, actor, payloadJson, now);
    }

    private void Record(
        string eventType,
        ReceiptState from,
        ReceiptState to,
        string actor,
        string? payloadJson,
        DateTimeOffset now) =>
        _events.Add(new ReceiptEvent(Id, eventType, from, to, actor, payloadJson, now));

    private static string Payload(object value) => JsonSerializer.Serialize(value);
}
