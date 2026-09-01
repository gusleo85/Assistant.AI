using Justina.Core.Domain;

namespace Justina.Recruitment.Domain;

/// <summary>
/// What the user asked for, normalized. Kept deliberately small: the Recruitment API contract is not
/// available yet, so this models only what the agent can reliably express today (plan risk R2).
/// </summary>
public sealed record CandidateSearchCriteria(
    string? Role,
    IReadOnlyCollection<string> Skills,
    string? Seniority,
    string? Location)
{
    public const int MaxSkills = 20;

    public static CandidateSearchCriteria Create(
        string? role,
        IEnumerable<string>? skills,
        string? seniority,
        string? location)
    {
        var normalizedSkills = (skills ?? [])
            .Select(s => s?.Trim())
            .Where(s => !string.IsNullOrEmpty(s))
            .Select(s => s!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (normalizedSkills.Count > MaxSkills)
        {
            throw new DomainException($"A search may list at most {MaxSkills} skills.");
        }

        return new CandidateSearchCriteria(
            string.IsNullOrWhiteSpace(role) ? null : role.Trim(),
            normalizedSkills,
            string.IsNullOrWhiteSpace(seniority) ? null : seniority.Trim(),
            string.IsNullOrWhiteSpace(location) ? null : location.Trim());
    }

    public bool IsEmpty => Role is null && Skills.Count == 0 && Seniority is null && Location is null;
}
