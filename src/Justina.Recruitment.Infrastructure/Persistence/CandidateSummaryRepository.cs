using Justina.Core.Application.Abstractions;
using Justina.Core.Domain.Messaging;
using Justina.Core.Infrastructure.Persistence;
using Justina.Recruitment.Application;
using Justina.Recruitment.Domain;
using Microsoft.EntityFrameworkCore;

namespace Justina.Recruitment.Infrastructure.Persistence;

public sealed class CandidateSummaryRepository(JustinaDbContext context) : ICandidateSummaryRepository
{
    public void Add(CandidateSummary summary) => context.Set<CandidateSummary>().Add(summary);

    public async Task<CandidateSummary?> GetAsync(Guid id, CancellationToken cancellationToken) =>
        await context.Set<CandidateSummary>()
            .FirstOrDefaultAsync(summary => summary.Id == id, cancellationToken)
            .ConfigureAwait(false);

    /// <summary>
    /// Oldest first, so a summary held while someone worked through a long receipt does not sit behind
    /// newer ones forever.
    /// </summary>
    public async Task<IReadOnlyList<CandidateSummary>> GetDeferredAsync(
        int limit,
        CancellationToken cancellationToken) =>
        await context.Set<CandidateSummary>()
            .Where(summary => summary.State == CandidateSummaryState.Deferred)
            .OrderBy(summary => summary.CreatedAtUtc)
            .Take(limit)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

    /// <summary>
    /// The most recently sent summary for this person — what a bare "Thursday 2pm" is answering.
    ///
    /// Most recent rather than "the only one": a hiring manager can be sent two summaries before
    /// replying to either, and answering the newer one is both the likelier reading and the one they can
    /// correct in a sentence.
    /// </summary>
    public async Task<CandidateSummary?> GetLatestSentAsync(
        ChannelKind channel,
        string recipientUserId,
        CancellationToken cancellationToken) =>
        await context.Set<CandidateSummary>()
            .Where(summary =>
                summary.Channel == channel
                && summary.RecipientUserId == recipientUserId
                && summary.State == CandidateSummaryState.Sent)
            .OrderByDescending(summary => summary.SentAtUtc)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
}

/// <summary>
/// Answers whether a recipient can be interrupted, from the conversation state the expense flow already
/// maintains.
///
/// It reads <c>ActiveWorkflow</c> without ever writing it. That is the whole point: the field holds one
/// value, expense owns it while a receipt is open, and recruitment must wait rather than take it.
/// </summary>
public sealed class ConversationAvailability(JustinaDbContext context) : IConversationAvailability
{
    public async Task<(Guid? ConversationId, bool IsFree)> CheckAsync(
        ChannelKind channel,
        string userId,
        CancellationToken cancellationToken)
    {
        // The most recently touched conversation for this person. Looked up rather than constructed,
        // because the channel's own conversation id is the gateway's to decide and guessing its shape
        // would silently check the wrong conversation — and then never defer anything.
        var conversation = await context.Conversations
            .AsNoTracking()
            .Where(record => record.Channel == channel && record.UserId == userId)
            .OrderByDescending(record => record.UpdatedAtUtc)
            .Select(record => new { record.Id, record.ActiveWorkflow })
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        // Somebody who has never written to Justina has nothing in progress to interrupt.
        if (conversation is null)
        {
            return (null, true);
        }

        return (conversation.Id, string.IsNullOrWhiteSpace(conversation.ActiveWorkflow));
    }
}
