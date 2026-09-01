using Justina.Core.Application.Messaging;
using Justina.Core.Domain.Identity;
using Justina.Core.Domain.Results;
using Justina.Recruitment.Domain;
using Microsoft.Extensions.DependencyInjection;

namespace Justina.Recruitment.Application;

public sealed record Candidate(string CandidateId, string DisplayName, string? Headline, string? Location);

public sealed record CandidateSearchResult(IReadOnlyList<Candidate> Candidates, int TotalMatches);

/// <summary>
/// The only route to the external Recruitment API. Defined now so phase 2 is additive; there is
/// deliberately no path from here to <c>IExpenseApiClient</c> (§54 rules 8 and 9).
/// </summary>
public interface IRecruitmentApiClient
{
    bool IsConfigured { get; }

    Task<Result<CandidateSearchResult>> SearchAsync(
        CandidateSearchCriteria criteria,
        CancellationToken cancellationToken);
}

public sealed record SearchCandidatesQuery(
    RequestContext Context,
    string? Role,
    IReadOnlyCollection<string>? Skills,
    string? Seniority,
    string? Location) : IQuery<CandidateSearchResult>, IRequireCapability
{
    public string RequiredCapability => Capabilities.RecruitmentSearch;
}

public sealed class SearchCandidatesQueryHandler(IRecruitmentApiClient client)
    : IQueryHandler<SearchCandidatesQuery, CandidateSearchResult>
{
    public Task<Result<CandidateSearchResult>> HandleAsync(
        SearchCandidatesQuery query,
        CancellationToken cancellationToken)
    {
        // Phase 1 ships routing, not execution. Saying so plainly is better than inventing a contract we
        // have not been given — and the request still never touches the Expense domain.
        if (!client.IsConfigured)
        {
            return Task.FromResult(Result.Failure<CandidateSearchResult>(
                ErrorCodes.NotAvailable,
                "Recruitment search is not connected yet, so I cannot run that search."));
        }

        var criteria = CandidateSearchCriteria.Create(query.Role, query.Skills, query.Seniority, query.Location);

        if (criteria.IsEmpty)
        {
            return Task.FromResult(Result.Failure<CandidateSearchResult>(
                ErrorCodes.Validation,
                "Tell me a role, a skill, a seniority or a location to search for."));
        }

        return client.SearchAsync(criteria, cancellationToken);
    }
}

public static class RecruitmentApplicationServiceCollectionExtensions
{
    public static IServiceCollection AddRecruitmentApplication(this IServiceCollection services)
    {
        services.AddQueryHandler<SearchCandidatesQuery, CandidateSearchResult, SearchCandidatesQueryHandler>();
        return services;
    }
}
