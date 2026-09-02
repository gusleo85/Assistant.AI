using Justina.Core.Domain.Messaging;

namespace Justina.Core.Domain.Identity;

/// <summary>
/// Capabilities are the unit of authorization. They are resolved deterministically in C# (§34);
/// the LLM never grants or infers one.
/// </summary>
public static class Capabilities
{
    public const string ExpenseSubmit = "expense.submit";
    public const string ExpenseRead = "expense.read";
    public const string RecruitmentSearch = "recruitment.search";

    /// <summary>Book an interview in the recruitment system.</summary>
    public const string RecruitmentSchedule = "recruitment.schedule";

    /// <summary>
    /// Change a candidate's status — shortlist, reject.
    ///
    /// Separate from scheduling on purpose: booking a slot is reversible and visible to everyone
    /// involved, while rejecting someone is neither. A person trusted to arrange interviews has not
    /// thereby been trusted to end applications.
    /// </summary>
    public const string RecruitmentStatus = "recruitment.status";

    public static readonly IReadOnlyCollection<string> All =
    [
        ExpenseSubmit,
        ExpenseRead,
        RecruitmentSearch,
        RecruitmentSchedule,
        RecruitmentStatus,
    ];
}

/// <summary>
/// The resolved Justina identity behind a channel user. Unknown channel users resolve to
/// <see cref="Anonymous"/>, which holds no capabilities.
/// </summary>
public sealed record UserContext(
    Guid PrincipalId,
    ChannelKind Channel,
    string UserId,
    string DisplayName,
    IReadOnlyCollection<string> Capabilities)
{
    public static UserContext Anonymous(ChannelKind channel, string userId) =>
        new(Guid.Empty, channel, userId, "unknown", []);

    public bool IsAuthenticated => PrincipalId != Guid.Empty;

    public bool Has(string capability) => Capabilities.Contains(capability);
}
