using Justina.Expense.Domain;

namespace Justina.Expense.Application.Abstractions;

/// <summary>
/// One repository for the Expense aggregate root — not a generic <c>IRepository&lt;T&gt;</c>, which would
/// be an abstraction with no second implementation and no testing value (§13).
/// </summary>
public interface IReceiptRepository
{
    Task<Receipt?> GetAsync(Guid receiptId, CancellationToken cancellationToken);

    Task<IReadOnlyList<Receipt>> GetByBatchAsync(Guid batchId, CancellationToken cancellationToken);

    /// <summary>The most recent non-terminal receipt for a conversation — what the agent is talking about.</summary>
    Task<Receipt?> GetActiveForConversationAsync(Guid conversationId, CancellationToken cancellationToken);

    void Add(Receipt receipt);

    void AddBatch(ReceiptBatch batch);
}
