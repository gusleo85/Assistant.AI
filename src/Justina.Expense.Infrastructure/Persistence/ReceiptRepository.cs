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
    /// The newest receipt that has not reached a terminal state — what "this receipt" means in conversation.
    /// </summary>
    public Task<Receipt?> GetActiveForConversationAsync(Guid conversationId, CancellationToken cancellationToken) =>
        context.Set<Receipt>()
            .Include(r => r.LineItems)
            .Where(r => r.ConversationId == conversationId
                && r.State != ReceiptState.Submitted
                && r.State != ReceiptState.Cancelled)
            .OrderByDescending(r => r.CreatedAtUtc)
            .FirstOrDefaultAsync(cancellationToken);

    public void Add(Receipt receipt) => context.Set<Receipt>().Add(receipt);

    public void AddBatch(ReceiptBatch batch) => context.Set<ReceiptBatch>().Add(batch);
}
