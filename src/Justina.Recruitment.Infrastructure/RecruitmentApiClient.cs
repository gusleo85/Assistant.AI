using Justina.Core.Domain.Results;
using Justina.Core.Application.Abstractions;
using Justina.Core.Infrastructure.Persistence;
using Justina.Recruitment.Application;
using Justina.Recruitment.Infrastructure.Persistence;
using Justina.Recruitment.Domain;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Justina.Recruitment.Infrastructure;

public sealed class RecruitmentApiOptions
{
    public const string SectionName = "RecruitmentApi";

    public string BaseUrl { get; set; } = string.Empty;

    public string ApiKey { get; set; } = string.Empty;

    public int TimeoutSeconds { get; set; } = 30;

    /// <summary>The stage's interview defaults. <c>{0}</c> job opening, <c>{1}</c> stage.</summary>
    public string HiringStagePath { get; set; } = "v1/JobOpening/{0}/HiringStage/{1}";

    /// <summary><c>{0}</c> candidate, <c>{1}</c> job opening, <c>{2}</c> stage.</summary>
    public string InterviewSchedulePath { get; set; } =
        "v1/Candidate/{0}/JobOpening/{1}/HiringStage/{2}/InterviewSchedule";

    /// <summary>
    /// <c>{0}</c> candidate, <c>{1}</c> status. Sent with PUT — the API kept its older GET for clients
    /// that still call it, but a status change is a write.
    /// </summary>
    public string CandidateStatusPath { get; set; } = "v1/Candidate/{0}/Status?status={1}";

    /// <summary>
    /// The status codes this recruitment system uses. They are configuration because they are the
    /// recruitment system's numbers, not ours, and because a model must never be the thing that turns
    /// "no" into a code — the wrong one rejects a candidate nobody rejected.
    ///
    /// Unset means the decision cannot be applied, and the assistant says so rather than guessing.
    /// </summary>
    public int? ShortlistStatus { get; set; }

    public int? RejectStatus { get; set; }
}

/// <summary>
/// Phase 1 implementation. The Recruitment API specification has not been supplied (plan risk R2), so this
/// reports that the capability is not connected rather than guessing a wire format. Phase 2 replaces the
/// body of <see cref="SearchAsync"/>; nothing above this class changes.
/// </summary>
public sealed class RecruitmentApiClient(
    IOptions<RecruitmentApiOptions> options,
    ILogger<RecruitmentApiClient> logger)
    : IRecruitmentApiClient
{
    private readonly RecruitmentApiOptions _options = options.Value;

    public bool IsConfigured => !string.IsNullOrWhiteSpace(_options.BaseUrl);

    public Task<Result<CandidateSearchResult>> SearchAsync(
        CandidateSearchCriteria criteria,
        CancellationToken cancellationToken)
    {
        logger.LogInformation("Recruitment search requested while the Recruitment API is not implemented");

        return Task.FromResult(Result.Failure<CandidateSearchResult>(
            ErrorCodes.NotAvailable,
            "Recruitment search is not available yet."));
    }
}

public static class RecruitmentInfrastructureServiceCollectionExtensions
{
    public static IServiceCollection AddRecruitmentInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.Configure<RecruitmentApiOptions>(configuration.GetSection(RecruitmentApiOptions.SectionName));
        services.AddScoped<IRecruitmentApiClient, RecruitmentApiClient>();

        // Candidate summaries: stored, deferred while their recipient is busy, and swept out when they
        // are free again.
        services.AddSingleton<IModelConfiguration, RecruitmentModelConfiguration>();
        services.AddScoped<ICandidateSummaryRepository, CandidateSummaryRepository>();
        services.AddScoped<IConversationAvailability, ConversationAvailability>();
        services.AddScoped<IRecruitmentAccessTokenProvider, ConfiguredRecruitmentAccessTokenProvider>();
        // Supplied as a plain object rather than IOptions: the application layer must not read
        // configuration, and these are its numbers to be told, not to look up.
        var recruitmentApi = configuration.GetSection(RecruitmentApiOptions.SectionName).Get<RecruitmentApiOptions>()
            ?? new RecruitmentApiOptions();

        services.AddSingleton(new CandidateStatusCodes
        {
            Shortlist = recruitmentApi.ShortlistStatus,
            Reject = recruitmentApi.RejectStatus,
        });

        services.AddHttpClient<IRecruitmentScheduler, RecruitmentScheduler>((provider, client) =>
        {
            var api = provider.GetRequiredService<IOptions<RecruitmentApiOptions>>().Value;

            if (!string.IsNullOrWhiteSpace(api.BaseUrl))
            {
                client.BaseAddress = new Uri($"{api.BaseUrl.TrimEnd('/')}/");
            }

            client.Timeout = TimeSpan.FromSeconds(api.TimeoutSeconds);
        });
        services.AddScoped<CandidateSummaryService>();
        services.AddHostedService<DeferredSummaryReleaseService>();

        return services;
    }
}
