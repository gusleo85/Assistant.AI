using System.Globalization;
using Justina.Core.Application.Messaging;
using Justina.Core.Domain.Identity;
using Justina.Core.Domain.Results;


namespace Justina.Recruitment.Application;

/// <summary>The status codes this recruitment system uses, supplied by configuration.</summary>
public sealed class CandidateStatusCodes
{
    public int? Shortlist { get; set; }

    public int? Reject { get; set; }
}

/// <summary>
/// Books the interview a hiring manager asked for.
///
/// The date and time are parsed here, not by the model: "Thursday 2pm" becomes a date somewhere, and
/// that somewhere should be code with a timezone and a test, not a sentence in a prompt.
/// </summary>
public sealed record ScheduleInterviewCommand(RequestContext Context, string Date, string Time)
    : ICommand<InterviewBooked>, IRequireCapability
{
    public string RequiredCapability => Capabilities.RecruitmentSchedule;
}

public sealed class ScheduleInterviewCommandHandler(RecruitmentConversationService conversations)
    : ICommandHandler<ScheduleInterviewCommand, InterviewBooked>
{
    public async Task<Result<InterviewBooked>> HandleAsync(
        ScheduleInterviewCommand command,
        CancellationToken cancellationToken)
    {
        if (!DateOnly.TryParseExact(command.Date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
        {
            return Result.Failure<InterviewBooked>(
                ErrorCodes.Validation,
                "I could not read that date. Please give it as a day, for example 2026-09-15.");
        }

        if (!TimeOnly.TryParseExact(command.Time, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var time))
        {
            return Result.Failure<InterviewBooked>(
                ErrorCodes.Validation,
                "I could not read that time. Please give it as 24-hour, for example 14:00.");
        }

        return await conversations
            .ScheduleAsync(
                command.Context.Channel,
                command.Context.User.UserId,
                date,
                time,
                cancellationToken)
            .ConfigureAwait(false);
    }
}

/// <summary>
/// Applies a hiring manager's decision to a candidate.
///
/// Destructive on purpose: a rejection is not something the person can undo from chat, and the tool that
/// carries it should say so.
/// </summary>
public sealed record UpdateCandidateStatusCommand(RequestContext Context, string Decision)
    : ICommand<string>, IRequireCapability
{
    public string RequiredCapability => Capabilities.RecruitmentStatus;
}

public sealed class UpdateCandidateStatusCommandHandler(
    RecruitmentConversationService conversations,
    CandidateStatusCodes statusCodes)
    : ICommandHandler<UpdateCandidateStatusCommand, string>
{
    public async Task<Result<string>> HandleAsync(
        UpdateCandidateStatusCommand command,
        CancellationToken cancellationToken)
    {
        // The word becomes a number here rather than in the prompt. A model that picked the code could
        // pick the wrong one, and rejecting a candidate is not a mistake that announces itself.
        var decision = command.Decision?.Trim().ToLowerInvariant() switch
        {
            "shortlist" or "shortlisted" or "yes" or "proceed" => CandidateDecision.Shortlist,
            "reject" or "rejected" or "no" or "decline" => CandidateDecision.Reject,
            _ => (CandidateDecision?)null,
        };

        if (decision is null)
        {
            return Result.Failure<string>(
                ErrorCodes.Validation,
                "I can shortlist or reject a candidate. Which did you mean?");
        }

        var code = decision == CandidateDecision.Shortlist
            ? statusCodes.Shortlist
            : statusCodes.Reject;

        if (code is null)
        {
            // Refused rather than guessed. An invented status code would move the candidate somewhere
            // nobody asked for, and the recruitment system would accept it.
            return Result.Failure<string>(
                ErrorCodes.NotAvailable,
                "I am not set up to change candidate statuses yet.");
        }

        return await conversations
            .UpdateStatusAsync(
                command.Context.Channel,
                command.Context.User.UserId,
                decision.Value,
                code.Value,
                cancellationToken)
            .ConfigureAwait(false);
    }
}
