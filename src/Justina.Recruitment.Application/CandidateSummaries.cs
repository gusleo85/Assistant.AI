using Justina.Core.Application.Abstractions;
using Justina.Core.Domain.Messaging;
using Justina.Core.Domain.Results;
using Justina.Recruitment.Domain;
using Microsoft.Extensions.Logging;

namespace Justina.Recruitment.Application;

/// <summary>What Recruitment-API asks Justina to tell somebody.</summary>
public sealed record CandidateSummaryRequest(
    string CandidateId,
    string? JobOpeningId,
    string? StageId,
    string? CandidateName,
    string? JobTitle,
    string SummaryText,
    string? CompanyId);

/// <summary>What happened to it.</summary>
public sealed record CandidateSummaryOutcome(Guid SummaryId, string State, string Recipient);

public interface ICandidateSummaryRepository
{
    void Add(CandidateSummary summary);

    Task<CandidateSummary?> GetAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>Summaries waiting for their recipient to finish something else.</summary>
    Task<IReadOnlyList<CandidateSummary>> GetDeferredAsync(int limit, CancellationToken cancellationToken);

    /// <summary>The summary a reply is most likely about: the last one sent to this person.</summary>
    Task<CandidateSummary?> GetLatestSentAsync(
        ChannelKind channel,
        string recipientUserId,
        CancellationToken cancellationToken);
}

/// <summary>
/// Decides whether a recipient can be interrupted right now.
///
/// The rule is deliberately not the model's to make: a receipt awaiting confirmation always outranks a
/// candidate summary. A person mid-"yes" should not have a second question arrive that "yes" could also
/// answer.
/// </summary>
public interface IConversationAvailability
{
    /// <returns>
    /// The recipient's current conversation and whether it is free. A person who has never written to
    /// Justina has no conversation and nothing to interrupt.
    /// </returns>
    Task<(Guid? ConversationId, bool IsFree)> CheckAsync(
        ChannelKind channel,
        string userId,
        CancellationToken cancellationToken);
}

/// <summary>
/// Takes a candidate summary, decides whether the recipient can be told now, and either sends it or
/// holds it until they are free.
///
/// Nothing here ever writes <c>ActiveWorkflow</c>. That field holds one value, the expense flow owns it
/// while a receipt is open, and a recruitment thread setting it would orphan a receipt mid-confirmation.
/// Recruitment waits its turn instead of taking one.
/// </summary>
public sealed class CandidateSummaryService(
    ICandidateSummaryRepository repository,
    IConversationAvailability availability,
    IProactiveMessenger messenger,
    IUnitOfWork unitOfWork,
    IClock clock,
    ILogger<CandidateSummaryService> logger)
{
    public async Task<Result<CandidateSummaryOutcome>> ReceiveAsync(
        ChannelRecipient recipient,
        CandidateSummaryRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(recipient);
        ArgumentNullException.ThrowIfNull(request);

        var (conversationId, isFree) = await availability
            .CheckAsync(recipient.Channel, recipient.UserId, cancellationToken)
            .ConfigureAwait(false);

        var summary = CandidateSummary.Create(
            recipient.Channel,
            recipient.UserId,
            conversationId,
            request.CandidateId,
            request.JobOpeningId,
            request.StageId,
            request.CandidateName,
            request.JobTitle,
            recipient.DisplayName,
            request.SummaryText,
            request.CompanyId,
            clock.UtcNow);

        repository.Add(summary);

        // Recorded before anything is sent. A summary that was delivered but never written down is a
        // conversation nobody can answer: the reply would arrive with no candidate attached.
        var saved = await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        if (saved.IsFailure)
        {
            return Result.Failure<CandidateSummaryOutcome>(saved.Error);
        }

        if (!isFree)
        {
            logger.LogInformation(
                "Holding the summary of candidate {CandidateId}: {Channel} user {UserId} is in the "
                + "middle of something else",
                request.CandidateId,
                recipient.Channel,
                recipient.UserId);

            return Result.Success(new CandidateSummaryOutcome(
                summary.Id,
                summary.State.ToString(),
                recipient.UserId));
        }

        return await DeliverAsync(summary, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Sends the summaries that were held back, for recipients who are now free.
    ///
    /// Swept rather than triggered by the expense flow finishing. A trigger would mean the expense side
    /// knowing that recruitment exists, and would lose anything deferred while the process was down;
    /// a sweep survives a restart and keeps the two domains ignorant of each other.
    /// </summary>
    public async Task<int> ReleaseDeferredAsync(int limit, CancellationToken cancellationToken)
    {
        var deferred = await repository.GetDeferredAsync(limit, cancellationToken).ConfigureAwait(false);
        var sent = 0;

        foreach (var summary in deferred)
        {
            var (_, isFree) = await availability
                .CheckAsync(summary.Channel, summary.RecipientUserId, cancellationToken)
                .ConfigureAwait(false);

            if (!isFree)
            {
                continue;
            }

            var delivered = await DeliverAsync(summary, cancellationToken).ConfigureAwait(false);

            if (delivered.IsSuccess)
            {
                sent++;
            }
        }

        return sent;
    }

    private async Task<Result<CandidateSummaryOutcome>> DeliverAsync(
        CandidateSummary summary,
        CancellationToken cancellationToken)
    {
        var message = Compose(summary);

        var sent = await messenger
            .SendAsync(new ChannelRecipient(summary.Channel, summary.RecipientUserId), message, cancellationToken)
            .ConfigureAwait(false);

        if (sent.IsFailure)
        {
            // Left Deferred rather than marked failed: the sweep will try again, and a gateway that was
            // briefly unreachable should not cost a hiring manager their summary.
            logger.LogWarning(
                "Could not deliver the summary of candidate {CandidateId}; it stays queued",
                summary.CandidateId);

            return Result.Failure<CandidateSummaryOutcome>(sent.Error);
        }

        summary.MarkSent(clock.UtcNow);

        var saved = await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        if (saved.IsFailure)
        {
            // The message is already on someone's phone. Reporting failure here would invite a retry
            // that sends it twice, so the send is reported as the success it was and the bookkeeping
            // failure is logged loudly.
            logger.LogError(
                "Sent the summary of candidate {CandidateId} but could not record it: {Error}",
                summary.CandidateId,
                saved.Error.Code);
        }

        return Result.Success(new CandidateSummaryOutcome(
            summary.Id,
            CandidateSummaryState.Sent.ToString(),
            summary.RecipientUserId));
    }

    /// <summary>
    /// The message as the hiring manager reads it: a greeting saying why it arrived, then the summary
    /// exactly as Recruitment-API composed it, then the question.
    ///
    /// The greeting matters more than it looks. This message is unprompted — the reader did not ask for
    /// it and may be nowhere near their desk — so it has to answer "who is this and why now" before it
    /// asks them for anything. The summary in the middle is never re-worded, and the question stays last
    /// and separate: the parts Justina wrote must be distinguishable from the parts a stranger's CV
    /// supplied.
    /// </summary>
    private static string Compose(CandidateSummary summary)
    {
        var greeting = string.IsNullOrWhiteSpace(summary.RecipientName)
            ? "Hello,"
            : $"Hello {summary.RecipientName},";

        var opening = string.IsNullOrWhiteSpace(summary.JobTitle)
            ? "we found an interesting candidate. Here are their details:"
            : $"we found an interesting candidate for the {summary.JobTitle} position. Here are their details:";

        var question = string.IsNullOrWhiteSpace(summary.CandidateName)
            ? "When would you like to interview them?"
            : $"When would you like to interview {summary.CandidateName}?";

        return $"{greeting} {opening}\n\n{summary.SummaryText}\n\n{question}";
    }
}
