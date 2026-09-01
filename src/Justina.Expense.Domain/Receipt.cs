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

    // EF Core materialization.
    private Receipt()
    {
        SourceMediaId = string.Empty;
    }

    private Receipt(Guid conversationId, string sourceMediaId, Guid? batchId, DateTimeOffset now)
    {
        Id = Guid.NewGuid();
        ConversationId = conversationId;
        SourceMediaId = sourceMediaId;
        BatchId = batchId;
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

    public string? Merchant { get; private set; }

    public DateOnly? ReceiptDate { get; private set; }

    public string? Currency { get; private set; }

    public decimal? Amount { get; private set; }

    public string? Category { get; private set; }

    public string? ReceiptNumber { get; private set; }

    public decimal? TaxAmount { get; private set; }

    /// <summary>The external system's expense id. Its presence is what makes a re-submit a no-op.</summary>
    public string? ExternalExpenseId { get; private set; }

    public string? FailureReason { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; private set; }

    public DateTimeOffset UpdatedAtUtc { get; private set; }

    public IReadOnlyList<ReceiptLineItem> LineItems => _lineItems;

    public IReadOnlyList<ReceiptEvent> Events => _events;

    public bool IsTerminal => State is ReceiptState.Submitted or ReceiptState.Cancelled;

    public static Receipt Create(Guid conversationId, string sourceMediaId, Guid? batchId, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceMediaId);

        var receipt = new Receipt(conversationId, sourceMediaId, batchId, now);
        receipt.Record("Created", ReceiptState.Received, ReceiptState.Received, "system", null, now);
        return receipt;
    }

    public void BeginExtraction(DateTimeOffset now)
    {
        Require(ReceiptState.Received, nameof(BeginExtraction));
        Transition(ReceiptState.Extracting, "ExtractionStarted", "system", null, now);
    }

    /// <summary>
    /// Joins this receipt to a batch. Only meaningful while extraction is running, because a document is
    /// not known to hold several receipts until Vision has read it (§25).
    /// </summary>
    public void AttachToBatch(Guid batchId, DateTimeOffset now)
    {
        Require(ReceiptState.Extracting, nameof(AttachToBatch));
        BatchId = batchId;
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

    /// <summary>True when every field the Expense API requires is present and well-formed.</summary>
    public bool IsSubmittable(out string? missingField)
    {
        if (string.IsNullOrWhiteSpace(Merchant))
        {
            missingField = nameof(Merchant);
            return false;
        }

        if (ReceiptDate is null)
        {
            missingField = nameof(ReceiptDate);
            return false;
        }

        if (!Money.IsValidCurrency(Currency))
        {
            missingField = nameof(Currency);
            return false;
        }

        if (Amount is null or <= 0m)
        {
            missingField = nameof(Amount);
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
        Amount = fields.Amount is { } amount ? decimal.Round(amount, 2, MidpointRounding.ToEven) : null;
        Category = fields.Category;
        ReceiptNumber = fields.ReceiptNumber;
        TaxAmount = fields.TaxAmount is { } tax ? decimal.Round(tax, 2, MidpointRounding.ToEven) : null;
    }

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
                break;

            case ReceiptField.Amount:
                Amount = RequirePositiveDecimal(change, ReceiptField.Amount);
                break;

            case ReceiptField.Category:
                Category = RequireString(change, ReceiptField.Category);
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
