using Justina.Core.Domain.Results;

namespace Justina.Recruitment.Application;

/// <summary>
/// The defaults a hiring stage carries for its interviews.
///
/// This is why the conversation is one question rather than four. The stage already says who
/// interviews, on what medium, and for how long — asking a hiring manager to repeat what the system
/// configured would be a worse experience than the web form they are avoiding.
/// </summary>
public sealed record HiringStageDefaults(
    string? InterviewTitle,
    IReadOnlyList<string> InterviewerIds,
    int? InterviewMediumId,
    string? Duration,
    string? PrivateNoteForInterviewer)
{
    /// <summary>
    /// The fields the stage could not supply, in the words a person would use. An empty list means
    /// nothing needs asking beyond the date and time.
    /// </summary>
    public IReadOnlyList<string> Missing()
    {
        var missing = new List<string>();

        if (InterviewerIds.Count == 0)
        {
            missing.Add("who should interview them");
        }

        if (InterviewMediumId is null)
        {
            missing.Add("whether it is in person or online");
        }

        if (string.IsNullOrWhiteSpace(Duration))
        {
            missing.Add("how long it should be");
        }

        return missing;
    }
}

/// <summary>An interview, as the recruitment system will record it.</summary>
public sealed record InterviewRequest(
    string CandidateId,
    string JobOpeningId,
    string StageId,
    DateOnly Date,
    TimeOnly Time,
    HiringStageDefaults Defaults,
    string? Title = null);

public sealed record InterviewScheduled(string InterviewId);

/// <summary>
/// The recruitment side of the conversation: what the stage already knows, and the two things a hiring
/// manager can ask for.
/// </summary>
public interface IRecruitmentScheduler
{
    /// <summary>Reads the defaults so the assistant only asks for what is genuinely missing.</summary>
    Task<Result<HiringStageDefaults>> GetStageDefaultsAsync(
        string jobOpeningId,
        string stageId,
        CancellationToken cancellationToken);

    Task<Result<InterviewScheduled>> ScheduleAsync(
        InterviewRequest request,
        CancellationToken cancellationToken);

    /// <summary>
    /// Shortlists or rejects a candidate. The status is a number the recruitment system defines; it is
    /// resolved from the person's words in C# rather than by the model, so "no" can never become the
    /// wrong code (§34).
    /// </summary>
    Task<Result> UpdateStatusAsync(
        string candidateId,
        int status,
        CancellationToken cancellationToken);
}
