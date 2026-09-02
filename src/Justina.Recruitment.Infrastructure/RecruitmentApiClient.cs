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
        services.AddScoped<CandidateSummaryService>();
        services.AddHostedService<DeferredSummaryReleaseService>();

        return services;
    }
}
