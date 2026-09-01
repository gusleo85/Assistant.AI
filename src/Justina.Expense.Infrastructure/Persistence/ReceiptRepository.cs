using Justina.Core.Infrastructure.Persistence;
using Justina.Expense.Application.Abstractions;
using Justina.Expense.Domain;
using Microsoft.EntityFrameworkCore;

namespace Justina.Expense.Infrastructure.Persistence;

public sealed class ReceiptRepository(JustinaDbContext context) : IReceiptRepository
{
    public Task<Receipt?> GetAsync(Guid receiptId, CancellationToken cancellationToken) =>
        context.Set<Receipt>()
            .Include(r => r.LineItems)
            .FirstOrDefaultAsync(r => r.Id == receiptId, cancellationToken);

    public async Task<IReadOnlyList<Receipt>> GetByBatchAsync(Guid batchId, CancellationToken cancellationToken) =>
        await context.Set<Receipt>()
            .Include(r => r.LineItems)
            .Where(r => r.BatchId == batchId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

    /// <summary>
    /// What "this receipt" means in conversation: the most recently received document that is still open,
    /// and within it the earliest receipt still needing attention.
    ///
    /// Ordering by sequence after the timestamp matters. Every receipt in a batch is created in the same
    /// instant, so the timestamp alone leaves the choice non-deterministic — a bare "yes" could land on
    /// any member. Sequence is reading order in the document, which is also the order the user is asked
    /// to confirm them in.
    /// </summary>
    public Task<Receipt?> GetActiveForConversationAsync(Guid conversationId, CancellationToken cancellationToken) =>
        context.Set<Receipt>()
            .Include(r => r.LineItems)
            .Where(r => r.ConversationId == conversationId
                && r.State != ReceiptState.Submitted
                && r.State != ReceiptState.Cancelled)
            .OrderByDescending(r => r.CreatedAtUtc)
            .ThenBy(r => r.SequenceInBatch)
            .FirstOrDefaultAsync(cancellationToken);

    public void Add(Receipt receipt) => context.Set<Receipt>().Add(receipt);

    public void AddBatch(ReceiptBatch batch) => context.Set<ReceiptBatch>().Add(batch);
}
