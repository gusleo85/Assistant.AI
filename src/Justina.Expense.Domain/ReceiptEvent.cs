namespace Justina.Expense.Domain;

/// <summary>
/// Append-only audit of every transition and edit (§40, §55). Written by the aggregate itself so no
/// state change can happen without a trace.
/// </summary>
public sealed class ReceiptEvent
{
    private ReceiptEvent()
    {
        EventType = string.Empty;
        Actor = string.Empty;
    }

    internal ReceiptEvent(
        Guid receiptId,
        string eventType,
        ReceiptState fromState,
        ReceiptState toState,
        string actor,
        string? payloadJson,
        DateTimeOffset createdAtUtc)
    {
        ReceiptId = receiptId;
        EventType = eventType;
        FromState = fromState;
        ToState = toState;
        Actor = actor;
        PayloadJson = payloadJson;
        CreatedAtUtc = createdAtUtc;
    }

    public long Id { get; private set; }

    public Guid ReceiptId { get; private set; }

    public string EventType { get; private set; }

    public ReceiptState FromState { get; private set; }

    public ReceiptState ToState { get; private set; }

    public string Actor { get; private set; }

    public string? PayloadJson { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; private set; }
}
