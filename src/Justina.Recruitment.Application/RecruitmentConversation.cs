using Justina.Core.Application.Abstractions;
using Justina.Core.Domain.Messaging;
using Justina.Core.Domain.Results;
using Microsoft.Extensions.Logging;

namespace Justina.Recruitment.Application;

/// <summary>
/// The decisions a hiring manager can reach in chat, in their own words rather than the recruitment
/// system's numbers.
/// </summary>
public enum CandidateDecision
{
    Shortlist,
    Reject,
}

/// <summary>What was booked, for the assistant to read back.</summary>
public sealed record InterviewBooked(string CandidateName, DateOnly Date, TimeOnly Time, string? InterviewId);

/// <summary>
/// Turns a hiring manager's reply into a call on the recruitment system.
///
/// It works from the summary Justina sent, not from what the reply says about itself: "Thursday 2pm"
/// names no candidate, and the assistant must not be the thing that decides which one was meant. The
/// most recent summary sent to that person is the answer, and it carries the candidate, opening and
/// stage it was built from.
/// </summary>
public sealed class RecruitmentConversationService(
    ICandidateSummaryRepository summaries,
    IRecruitmentScheduler scheduler,
    IUnitOfWork unitOfWork,
    IClock clock,
    ILogger<RecruitmentConversationService> logger)
{
    /// <summary>
    /// Books the interview the hiring manager asked for, filling everything except the date and time
    /// from the hiring stage.
    /// </summary>
    public async Task<Result<InterviewBooked>> ScheduleAsync(
        ChannelKind channel,
        string userId,
        DateOnly date,
        TimeOnly time,
        CancellationToken cancellationToken)
    {
        var summary = await summaries
            .GetLatestSentAsync(channel, userId, cancellationToken)
            .ConfigureAwait(false);

        if (summary is null)
        {
            return Result.Failure<InterviewBooked>(
                ErrorCodes.NotFound,
                "I do not have a candidate waiting on you at the moment.");
        }

        if (string.IsNullOrWhiteSpace(summary.JobOpeningId) || string.IsNullOrWhiteSpace(summary.StageId))
        {
            // Without both, the recruitment system has nowhere to hang the interview. Refusing beats
            // booking it against a guess.
            return Result.Failure<InterviewBooked>(
                ErrorCodes.NotAvailable,
                "That candidate is not attached to a job opening and stage I can book against.");
        }

        var defaults = await scheduler
            .GetStageDefaultsAsync(summary.JobOpeningId, summary.StageId, cancellationToken)
            .ConfigureAwait(false);

        if (defaults.IsFailure)
        {
            return Result.Failure<InterviewBooked>(defaults.Error);
        }

        var missing = defaults.Value.Missing();

        if (missing.Count > 0)
        {
            // Asked for, never invented. An interview booked with a made-up interviewer or length is
            // worse than one that had to be arranged in the web app.
            return Result.Failure<InterviewBooked>(
                ErrorCodes.Validation,
                $"This hiring stage does not say {string.Join(", ", missing)}. "
                + "Please set that on the stage, or arrange this interview in the recruitment app.");
        }

        var scheduled = await scheduler
            .ScheduleAsync(
                new InterviewRequest(
                    summary.CandidateId,
                    summary.JobOpeningId,
                    summary.StageId,
                    date,
                    time,
                    defaults.Value),
                cancellationToken)
            .ConfigureAwait(false);

        if (scheduled.IsFailure)
        {
            return Result.Failure<InterviewBooked>(scheduled.Error);
        }

        summary.MarkScheduled(scheduled.Value.InterviewId, clock.UtcNow);

        var saved = await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        if (saved.IsFailure)
        {
            // The interview exists in the recruitment system. Reporting failure would invite a retry
            // that books a second one, so the booking is reported and the bookkeeping failure logged.
            logger.LogError(
                "Booked interview {InterviewId} but could not record it against summary {SummaryId}",
                scheduled.Value.InterviewId,
                summary.Id);
        }

        return Result.Success(new InterviewBooked(
            summary.CandidateName ?? "the candidate",
            date,
            time,
            scheduled.Value.InterviewId));
    }

    /// <summary>
    /// Applies a decision to the candidate the last summary was about.
    ///
    /// The decision arrives as a word and becomes a number here. The recruitment system's status codes
    /// are configuration, not something a model should be choosing: "no" turning into the wrong code
    /// would reject a candidate nobody rejected.
    /// </summary>
    public async Task<Result<string>> UpdateStatusAsync(
        ChannelKind channel,
        string userId,
        CandidateDecision decision,
        int statusCode,
        CancellationToken cancellationToken)
    {
        var summary = await summaries
            .GetLatestSentAsync(channel, userId, cancellationToken)
            .ConfigureAwait(false);

        if (summary is null)
        {
            return Result.Failure<string>(
                ErrorCodes.NotFound,
                "I do not have a candidate waiting on you at the moment.");
        }

        var updated = await scheduler
            .UpdateStatusAsync(summary.CandidateId, statusCode, cancellationToken)
            .ConfigureAwait(false);

        if (updated.IsFailure)
        {
            return Result.Failure<string>(updated.Error);
        }

        summary.MarkStatusUpdated(clock.UtcNow);

        await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        logger.LogInformation(
            "Candidate {CandidateId} was {Decision} by {Channel} user {UserId}",
            summary.CandidateId,
            decision,
            channel,
            userId);

        return Result.Success(summary.CandidateName ?? "the candidate");
    }
}
