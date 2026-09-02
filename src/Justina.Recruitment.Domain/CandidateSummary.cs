using Justina.Core.Domain;
using Justina.Core.Domain.Messaging;

namespace Justina.Recruitment.Domain;

/// <summary>
/// Where a candidate summary has got to.
///
/// <c>Deferred</c> exists because of the expense flow. A hiring manager confirming a receipt is mid
/// sentence with Justina, and dropping a candidate summary into that conversation would leave "yes"
/// meaning two different things. So the summary waits, and is sent when the conversation is free.
/// </summary>
public enum CandidateSummaryState
{
    /// <summary>Held back: the recipient is in the middle of something else.</summary>
    Deferred = 0,

    /// <summary>Delivered, and the hiring manager has been asked when they would like to interview.</summary>
    Sent = 1,

    /// <summary>An interview was booked from their answer.</summary>
    Scheduled = 2,

    /// <summary>They changed the candidate's status instead.</summary>
    StatusUpdated = 3,

    /// <summary>Given up on — the message could not be delivered.</summary>
    Failed = 4,

    /// <summary>Closed without action.</summary>
    Cancelled = 5,
}

/// <summary>
/// One candidate summary sent to a hiring manager, and whatever came of it.
///
/// It exists so the reply means something. Without a record, "Thursday 2pm" arrives with no candidate
/// attached, and the assistant would have to guess which of the summaries it sent was being answered.
/// The identifiers needed to act — candidate, job opening, stage — travel with it from the moment
/// Recruitment-API hands it over.
/// </summary>
public sealed class CandidateSummary
{
    // EF Core materialization.
    private CandidateSummary()
    {
        CandidateId = string.Empty;
        SummaryText = string.Empty;
        RecipientUserId = string.Empty;
    }

    private CandidateSummary(
        Guid id,
        ChannelKind channel,
        string recipientUserId,
        Guid? conversationId,
        string candidateId,
        string? jobOpeningId,
        string? stageId,
        string? candidateName,
        string? jobTitle,
        string? recipientName,
        string summaryText,
        string? companyId,
        DateTimeOffset now)
    {
        Id = id;
        Channel = channel;
        RecipientUserId = recipientUserId;
        ConversationId = conversationId;
        CandidateId = candidateId;
        JobOpeningId = jobOpeningId;
        StageId = stageId;
        CandidateName = candidateName;
        JobTitle = jobTitle;
        RecipientName = recipientName;
        SummaryText = summaryText;
        CompanyId = companyId;
        State = CandidateSummaryState.Deferred;
        CreatedAtUtc = now;
        UpdatedAtUtc = now;
    }

    public Guid Id { get; private set; }

    public ChannelKind Channel { get; private set; }

    /// <summary>The channel's own id for the person being told.</summary>
    public string RecipientUserId { get; private set; }

    /// <summary>
    /// The conversation this will land in, when one is already known. Null means the recipient has never
    /// spoken to Justina, in which case there is nothing in progress to interrupt.
    /// </summary>
    public Guid? ConversationId { get; private set; }

    public string CandidateId { get; private set; }

    public string? JobOpeningId { get; private set; }

    /// <summary>The candidate's current active stage — where an interview would be scheduled.</summary>
    public string? StageId { get; private set; }

    public string? CandidateName { get; private set; }

    /// <summary>The role they applied for, so the greeting can name the position.</summary>
    public string? JobTitle { get; private set; }

    /// <summary>
    /// The hiring manager, as Justina knows them. Held on the summary rather than looked up when sending
    /// so a message says the same thing when it is finally delivered as it would have at the time — a
    /// deferred summary can sit for a while, and people get renamed.
    /// </summary>
    public string? RecipientName { get; private set; }

    /// <summary>
    /// The summary as Recruitment-API composed it. Stored verbatim and never re-worded: it is built from
    /// a CV a stranger uploaded, so it is content to show, not instructions to follow (§38).
    /// </summary>
    public string SummaryText { get; private set; }

    public string? CompanyId { get; private set; }

    public CandidateSummaryState State { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; private set; }

    public DateTimeOffset UpdatedAtUtc { get; private set; }

    public DateTimeOffset? SentAtUtc { get; private set; }

    /// <summary>The interview the Recruitment API created, once one exists.</summary>
    public string? ExternalInterviewId { get; private set; }

    public string? FailureReason { get; private set; }

    /// <summary>Optimistic concurrency: two releases of the same deferred summary must not both send.</summary>
    public byte[]? RowVersion { get; private set; }

    public bool IsTerminal =>
        State is CandidateSummaryState.Scheduled
            or CandidateSummaryState.StatusUpdated
            or CandidateSummaryState.Failed
            or CandidateSummaryState.Cancelled;

    public static CandidateSummary Create(
        ChannelKind channel,
        string recipientUserId,
        Guid? conversationId,
        string candidateId,
        string? jobOpeningId,
        string? stageId,
        string? candidateName,
        string? jobTitle,
        string? recipientName,
        string summaryText,
        string? companyId,
        DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(recipientUserId);
        ArgumentException.ThrowIfNullOrWhiteSpace(candidateId);
        ArgumentException.ThrowIfNullOrWhiteSpace(summaryText);

        return new CandidateSummary(
            Guid.CreateVersion7(),
            channel,
            recipientUserId,
            conversationId,
            candidateId,
            jobOpeningId,
            stageId,
            candidateName,
            jobTitle,
            recipientName,
            summaryText,
            companyId,
            now);
    }

    /// <summary>
    /// Records that the message reached the hiring manager, and that they have been asked when they
    /// would like to interview.
    /// </summary>
    public void MarkSent(DateTimeOffset now)
    {
        if (State != CandidateSummaryState.Deferred)
        {
            throw new DomainException($"A summary in {State} has already been sent.");
        }

        State = CandidateSummaryState.Sent;
        SentAtUtc = now;
        UpdatedAtUtc = now;
    }

    public void MarkScheduled(string externalInterviewId, DateTimeOffset now)
    {
        if (State != CandidateSummaryState.Sent)
        {
            throw new DomainException($"A summary in {State} cannot have an interview booked against it.");
        }

        ExternalInterviewId = externalInterviewId;
        State = CandidateSummaryState.Scheduled;
        UpdatedAtUtc = now;
    }

    public void MarkStatusUpdated(DateTimeOffset now)
    {
        if (State != CandidateSummaryState.Sent)
        {
            throw new DomainException($"A summary in {State} cannot record a status change.");
        }

        State = CandidateSummaryState.StatusUpdated;
        UpdatedAtUtc = now;
    }

    /// <summary>
    /// Gives up on delivery. Kept as a record rather than deleted: someone pressed a button and is
    /// entitled to find out later that nothing reached anyone.
    /// </summary>
    public void MarkFailed(string reason, DateTimeOffset now)
    {
        State = CandidateSummaryState.Failed;
        FailureReason = reason;
        UpdatedAtUtc = now;
    }

    public void Cancel(DateTimeOffset now)
    {
        if (IsTerminal)
        {
            return;
        }

        State = CandidateSummaryState.Cancelled;
        UpdatedAtUtc = now;
    }
}
