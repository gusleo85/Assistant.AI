using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Justina.Core.Domain.Results;
using Justina.Recruitment.Application;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Justina.Recruitment.Infrastructure;

/// <summary>
/// Calls Recruitment-API for the two things a hiring manager can ask for in chat, and reads the stage
/// defaults that keep the conversation short.
///
/// Authenticated with a company system token, the same credential the expense integration uses. The
/// acting user is not sent: Recruitment-API resolves a system token to its configured service account,
/// because letting a caller name the acting user would let one service act as any employee.
/// </summary>
public sealed class RecruitmentScheduler(
    HttpClient httpClient,
    IOptions<RecruitmentApiOptions> options,
    IRecruitmentAccessTokenProvider tokens,
    ILogger<RecruitmentScheduler> logger)
    : IRecruitmentScheduler
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly RecruitmentApiOptions _options = options.Value;

    public async Task<Result<HiringStageDefaults>> GetStageDefaultsAsync(
        string jobOpeningId,
        string stageId,
        CancellationToken cancellationToken)
    {
        var path = string.Format(
            CultureInfo.InvariantCulture,
            _options.HiringStagePath,
            jobOpeningId,
            stageId);

        var response = await SendAsync(HttpMethod.Get, path, null, cancellationToken).ConfigureAwait(false);

        if (response.IsFailure)
        {
            return Result.Failure<HiringStageDefaults>(response.Error);
        }

        try
        {
            var stage = JsonNode.Parse(response.Value)?.AsObject();

            if (stage is null)
            {
                return Result.Failure<HiringStageDefaults>(
                    ErrorCodes.ExternalApiFailed,
                    "I could not read the interview settings for this stage.");
            }

            return Result.Success(new HiringStageDefaults(
                stage["interviewTitle"]?.GetValue<string>(),
                ReadIds(stage["interviewerIds"]),
                stage["interviewMediumId"]?.GetValue<int?>(),
                stage["duration"]?.GetValue<string>(),
                stage["privateNoteForInterviewer"]?.GetValue<string>()));
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or FormatException)
        {
            logger.LogError(exception, "Could not read the hiring stage {StageId}", stageId);

            return Result.Failure<HiringStageDefaults>(
                ErrorCodes.ExternalApiFailed,
                "I could not read the interview settings for this stage.");
        }
    }

    public async Task<Result<InterviewScheduled>> ScheduleAsync(
        InterviewRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var path = string.Format(
            CultureInfo.InvariantCulture,
            _options.InterviewSchedulePath,
            request.CandidateId,
            request.JobOpeningId,
            request.StageId);

        var interviewers = new JsonArray();

        foreach (var interviewer in request.Defaults.InterviewerIds)
        {
            interviewers.Add(interviewer);
        }

        var payload = new JsonObject
        {
            // The stage's own title when it has one: an interview named after the stage is what the
            // recruiter would have got from the web form.
            ["interviewScheduleTitle"] = request.Title
                ?? request.Defaults.InterviewTitle
                ?? "Interview",
            ["interviewerIds"] = interviewers,
            ["interviewMediumId"] = request.Defaults.InterviewMediumId,
            ["duration"] = request.Defaults.Duration,
            ["interviewDate"] = request.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            ["interviewTime"] = request.Time.ToString("HH:mm", CultureInfo.InvariantCulture),
            ["privateNoteForInterviewer"] = request.Defaults.PrivateNoteForInterviewer ?? string.Empty,
            ["isCancelled"] = false,
        };

        var response = await SendAsync(HttpMethod.Post, path, payload, cancellationToken).ConfigureAwait(false);

        if (response.IsFailure)
        {
            return Result.Failure<InterviewScheduled>(response.Error);
        }

        var id = ReadId(response.Value);

        logger.LogInformation(
            "Scheduled an interview for candidate {CandidateId} on {Date} at {Time}",
            request.CandidateId,
            request.Date,
            request.Time);

        return Result.Success(new InterviewScheduled(id ?? string.Empty));
    }

    public async Task<Result> UpdateStatusAsync(
        string candidateId,
        int status,
        CancellationToken cancellationToken)
    {
        var path = string.Format(CultureInfo.InvariantCulture, _options.CandidateStatusPath, candidateId, status);

        // PUT, not the older GET. A status change is a write, and a GET that writes is retried by
        // proxies and prefetched by browsers — a candidate rejected by a page load.
        var response = await SendAsync(HttpMethod.Put, path, new JsonObject(), cancellationToken)
            .ConfigureAwait(false);

        return response.IsFailure ? Result.Failure(response.Error) : Result.Success();
    }

    private async Task<Result<string>> SendAsync(
        HttpMethod method,
        string path,
        JsonNode? body,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_options.BaseUrl))
        {
            return Result.Failure<string>(ErrorCodes.NotAvailable, "The recruitment system is not configured.");
        }

        var token = await tokens.GetAsync(cancellationToken).ConfigureAwait(false);

        if (token.IsFailure)
        {
            return Result.Failure<string>(token.Error);
        }

        using var request = new HttpRequestMessage(method, path);

        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {token.Value}");

        try
        {
            using var response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
            var content = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            if (response.IsSuccessStatusCode)
            {
                return Result.Success(content);
            }

            // Provider detail is logged truncated and never relayed to the user (§38).
            logger.LogError(
                "Recruitment-API answered {StatusCode} for {Path}: {Body}",
                (int)response.StatusCode,
                path,
                content.Length > 300 ? content[..300] : content);

            return Result.Failure<string>(
                response.StatusCode switch
                {
                    System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden =>
                        new Error(ErrorCodes.Unauthorized, "The recruitment system refused that."),
                    System.Net.HttpStatusCode.NotFound =>
                        new Error(ErrorCodes.NotFound, "I could not find that candidate in the recruitment system."),
                    System.Net.HttpStatusCode.BadRequest =>
                        new Error(ErrorCodes.Validation, "The recruitment system rejected those details."),
                    _ => new Error(ErrorCodes.ExternalApiFailed, "The recruitment system could not do that just now."),
                });
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            logger.LogError(exception, "Could not reach Recruitment-API for {Path}", path);

            return Result.Failure<string>(
                ErrorCodes.ExternalApiFailed,
                "I could not reach the recruitment system. It can be retried.");
        }
    }

    private static IReadOnlyList<string> ReadIds(JsonNode? node)
    {
        if (node is not JsonArray array)
        {
            return [];
        }

        var ids = new List<string>();

        foreach (var item in array)
        {
            // The API returns interviewers as objects with an id, and sometimes as bare ids. Both are
            // read rather than assuming one: an empty interviewer list turns a one-question
            // conversation into a four-question one.
            var id = item is JsonObject entry
                ? entry["id"]?.GetValue<string>() ?? entry["userId"]?.GetValue<string>()
                : item?.GetValue<string>();

            if (!string.IsNullOrWhiteSpace(id))
            {
                ids.Add(id);
            }
        }

        return ids;
    }

    private static string? ReadId(string body)
    {
        try
        {
            var node = JsonNode.Parse(body);

            return node?["id"]?.GetValue<string>()
                ?? node?["data"]?["id"]?.GetValue<string>();
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or FormatException)
        {
            return null;
        }
    }
}

/// <summary>
/// The credential for Recruitment-API, from configuration.
///
/// A fixed token for now, deliberately: minting a company token per call means the JustLogin identity
/// client, which lives behind the expense infrastructure, and recruitment must not depend on expense.
/// Moving that provider into the core infrastructure is the change that makes this mint its own — until
/// then a configured token keeps the two domains apart, which matters more than saving a config value.
/// </summary>
public sealed class ConfiguredRecruitmentAccessTokenProvider(IOptions<RecruitmentApiOptions> options)
    : IRecruitmentAccessTokenProvider
{
    private readonly RecruitmentApiOptions _options = options.Value;

    public Task<Result<string>> GetAsync(CancellationToken cancellationToken) =>
        Task.FromResult(string.IsNullOrWhiteSpace(_options.ApiKey)
            ? Result.Failure<string>(
                ErrorCodes.NotAvailable,
                "The recruitment system is not configured with a credential.")
            : Result.Success(_options.ApiKey));
}
