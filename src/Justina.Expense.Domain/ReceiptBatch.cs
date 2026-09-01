namespace Justina.Expense.Domain;

/// <summary>
/// Groups the receipts found in one source document. Its existence is what stops several receipts from
/// silently becoming one expense (§25): each member is confirmed and submitted on its own.
/// </summary>
public sealed class ReceiptBatch
{
    private readonly List<Receipt> _receipts = [];

    private ReceiptBatch()
    {
        SourceMediaId = string.Empty;
    }

    private ReceiptBatch(Guid conversationId, string sourceMediaId, DateTimeOffset now)
    {
        Id = Guid.NewGuid();
        ConversationId = conversationId;
        SourceMediaId = sourceMediaId;
        CreatedAtUtc = now;
    }

    public Guid Id { get; private set; }

    public Guid ConversationId { get; private set; }

    public string SourceMediaId { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; private set; }

    public IReadOnlyList<Receipt> Receipts => _receipts;

    public int Count => _receipts.Count;

    public static ReceiptBatch Create(Guid conversationId, string sourceMediaId, DateTimeOffset now) =>
        new(conversationId, sourceMediaId, now);

    public Receipt AddReceipt(DateTimeOffset now)
    {
        var receipt = Receipt.Create(ConversationId, SourceMediaId, Id, now);
        _receipts.Add(receipt);
        return receipt;
    }
}
