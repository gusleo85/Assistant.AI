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

    public static readonly IReadOnlyCollection<string> All =
    [
        ExpenseSubmit,
        ExpenseRead,
        RecruitmentSearch,
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
