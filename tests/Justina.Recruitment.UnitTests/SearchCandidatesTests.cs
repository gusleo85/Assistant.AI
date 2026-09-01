using Justina.Core.Application.Messaging;
using Justina.Core.Domain.Identity;
using Justina.Core.Domain.Messaging;
using Justina.Core.Domain.Observability;
using Justina.Core.Domain.Results;
using Justina.Recruitment.Application;
using Justina.Recruitment.Domain;
using NSubstitute;
using Shouldly;

namespace Justina.Recruitment.UnitTests;

public class CandidateSearchCriteriaTests
{
    [Fact]
    public void Skills_are_trimmed_and_de_duplicated_case_insensitively()
    {
        var criteria = CandidateSearchCriteria.Create("Engineer", ["  C#  ", "c#", "SQL"], null, null);

        criteria.Skills.Count.ShouldBe(2);
        criteria.Skills.ShouldContain("C#");
        criteria.Skills.ShouldContain("SQL");
    }

    [Fact]
    public void An_empty_request_is_recognised_as_empty()
    {
        CandidateSearchCriteria.Create(null, null, null, null).IsEmpty.ShouldBeTrue();
    }

    [Fact]
    public void Too_many_skills_is_refused()
    {
        var skills = Enumerable.Range(0, CandidateSearchCriteria.MaxSkills + 1).Select(i => $"skill-{i}");

        Should.Throw<Core.Domain.DomainException>(() =>
            CandidateSearchCriteria.Create("Engineer", skills, null, null));
    }
}

public class SearchCandidatesQueryHandlerTests
{
    private readonly IRecruitmentApiClient _client = Substitute.For<IRecruitmentApiClient>();

    private static RequestContext Context(params string[] capabilities) =>
        new(
            new UserContext(Guid.NewGuid(), ChannelKind.Telegram, "user-1", "Test User", capabilities),
            ChannelKind.Telegram,
            "conversation-1",
            CorrelationId.New());

    [Fact]
    public async Task An_unconfigured_recruitment_api_reports_unavailable_rather_than_inventing_results()
    {
        _client.IsConfigured.Returns(false);

        var handler = new SearchCandidatesQueryHandler(_client);

        var result = await handler.HandleAsync(
            new SearchCandidatesQuery(Context(Capabilities.RecruitmentSearch), "Senior .NET", null, null, null),
            default);

        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe(ErrorCodes.NotAvailable);
        await _client.DidNotReceiveWithAnyArgs().SearchAsync(default!, default);
    }

    [Fact]
    public async Task A_request_with_no_criteria_asks_for_more_instead_of_searching_for_everything()
    {
        _client.IsConfigured.Returns(true);

        var handler = new SearchCandidatesQueryHandler(_client);

        var result = await handler.HandleAsync(
            new SearchCandidatesQuery(Context(Capabilities.RecruitmentSearch), null, null, null, null),
            default);

        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe(ErrorCodes.Validation);
        await _client.DidNotReceiveWithAnyArgs().SearchAsync(default!, default);
    }

    [Fact]
    public async Task A_valid_request_reaches_the_recruitment_client()
    {
        _client.IsConfigured.Returns(true);
        _client.SearchAsync(Arg.Any<CandidateSearchCriteria>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new CandidateSearchResult([], 0)));

        var handler = new SearchCandidatesQueryHandler(_client);

        var result = await handler.HandleAsync(
            new SearchCandidatesQuery(Context(Capabilities.RecruitmentSearch), "Senior .NET", ["C#"], null, "Jakarta"),
            default);

        result.IsSuccess.ShouldBeTrue();
        await _client.Received(1).SearchAsync(Arg.Any<CandidateSearchCriteria>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// Business rule 8: a recruitment request must never reach the Expense system. The structural guarantee
    /// is the missing project reference (asserted in the architecture tests); this covers the behaviour.
    /// </summary>
    [Fact]
    public async Task A_caller_without_the_recruitment_capability_is_refused_by_the_decorator()
    {
        _client.IsConfigured.Returns(true);

        var decorated = new AuthorizationQueryDecorator<SearchCandidatesQuery, CandidateSearchResult>(
            new SearchCandidatesQueryHandler(_client));

        var result = await decorated.HandleAsync(
            new SearchCandidatesQuery(Context(Capabilities.ExpenseSubmit), "Senior .NET", null, null, null),
            default);

        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe(ErrorCodes.Unauthorized);
        await _client.DidNotReceiveWithAnyArgs().SearchAsync(default!, default);
    }
}
