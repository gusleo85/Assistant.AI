using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Justina.Core.Domain.Results;
using Justina.Expense.Application.Abstractions;
using Justina.Expense.Infrastructure.MockData;
using JustLogin.Identity.SDK.Startup.Configurations;
using JustLogin.Identity.SDK.SystemToken.Interfaces;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Justina.Expense.Infrastructure.Api;

/// <summary>
/// Maps a company GUID to the <c>CompanyID</c> the identity server's token request expects.
///
/// These are two different identifiers for one company, and the distinction is the whole reason the
/// membership API is in this flow: Justina knows a company by its GUID, and the token endpoint only
/// accepts the other one.
///
/// It is a seam of its own because the two halves can be believed independently. The identity server is
/// real and reachable today; the membership lookup needs data we can stand in for exactly, since the
/// answer for our one dev company is a constant.
/// </summary>
public interface IJustLoginCompanyDirectory
{
    Task<Result<string>> GetCompanyIdAsync(ExpenseTenant tenant, CancellationToken cancellationToken);
}

/// <summary>
/// Serves the mapping from the embedded <c>membership-company.json</c>.
///
/// It refuses when the fixture has no <c>companyId</c> rather than falling back to the company GUID. A
/// GUID sent as CompanyID does not fail: the identity server issues a token without company claims, and
/// the expense call then fails much later with a 401 that reads like an outage.
/// </summary>
public sealed class MockJustLoginCompanyDirectory(ILogger<MockJustLoginCompanyDirectory> logger)
    : IJustLoginCompanyDirectory
{
    public Task<Result<string>> GetCompanyIdAsync(ExpenseTenant tenant, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tenant);

        var record = StubMembershipCompany.Current;

        if (record is null || string.IsNullOrWhiteSpace(record.CompanyId))
        {
            logger.LogError(
                "MOCK membership: membership-company.json has no companyId, so no company token can be " +
                "requested. Fill it from GET membership/v2/companies/{CompanyGuid}",
                tenant.CompanyGuid);

            return Task.FromResult(Result.Failure<string>(
                ErrorCodes.NotAvailable,
                "The expense system is not fully configured for this company yet."));
        }

        if (!string.Equals(record.CompanyGuid, tenant.CompanyGuid, StringComparison.OrdinalIgnoreCase))
        {
            // One fixture, one company. Answering for a company it does not describe would put another
            // organization's expenses behind this company's token.
            logger.LogError(
                "MOCK membership: asked for {Requested} but the fixture describes {Known}",
                tenant.CompanyGuid,
                record.CompanyGuid);

            return Task.FromResult(Result.Failure<string>(
                ErrorCodes.NotAvailable,
                "The expense system is not configured for this company."));
        }

        logger.LogWarning(
            "MOCK membership: {CompanyGuid} resolved to CompanyID {CompanyId} from embedded mock data, " +
            "not from the membership API",
            record.CompanyGuid,
            record.CompanyId);

        return Task.FromResult(Result.Success(record.CompanyId));
    }

    public sealed record MembershipCompanyRecord
    {
        [JsonPropertyName("companyGuid")]
        public string CompanyGuid { get; init; } = string.Empty;

        [JsonPropertyName("companyId")]
        public string CompanyId { get; init; } = string.Empty;

        [JsonPropertyName("companyName")]
        public string CompanyName { get; init; } = string.Empty;
    }
}

/// <summary>
/// The real lookup: <c>GET membership/v2/companies/{companyGuid}</c>, authenticated with the system
/// token.
///
/// The call is made here rather than through the SDK because the SDK does not expose it — its membership
/// lookup is private to <c>GetCompanySystemToken</c>, which also mints the token and so cannot be used
/// to answer "what is this company's CompanyID". The request is byte-for-byte the one the SDK makes.
/// </summary>
public sealed class MembershipJustLoginCompanyDirectory(
    HttpClient httpClient,
    ISingletonAuthenticationClient systemTokens,
    IOptions<IdentityConfiguration> identityOptions,
    ILogger<MembershipJustLoginCompanyDirectory> logger)
    : IJustLoginCompanyDirectory
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IdentityConfiguration _identity = identityOptions.Value;

    public async Task<Result<string>> GetCompanyIdAsync(ExpenseTenant tenant, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tenant);

        var path = string.Format(
            CultureInfo.InvariantCulture,
            _identity.MembershipCompanyEndpoint,
            tenant.CompanyGuid);

        try
        {
            var systemToken = await systemTokens.GetSystemToken(cancellationToken).ConfigureAwait(false);

            if (string.IsNullOrWhiteSpace(systemToken?.AccessToken))
            {
                return Result.Failure<string>(
                    ErrorCodes.ExternalApiFailed,
                    "I could not sign in to the expense system just now.");
            }

            using var request = new HttpRequestMessage(HttpMethod.Get, path);
            request.Headers.Authorization = new AuthenticationHeaderValue(
                systemToken.TokenType ?? "Bearer",
                systemToken.AccessToken);

            using var response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                logger.LogError(
                    "Membership returned {StatusCode} for company {CompanyGuid}",
                    (int)response.StatusCode,
                    tenant.CompanyGuid);

                return Result.Failure<string>(
                    ErrorCodes.ExternalApiFailed,
                    "I could not look up your company in the expense system.");
            }

            var company = await response.Content
                .ReadFromJsonAsync<MockJustLoginCompanyDirectory.MembershipCompanyRecord>(JsonOptions, cancellationToken)
                .ConfigureAwait(false);

            if (company is null || string.IsNullOrWhiteSpace(company.CompanyId))
            {
                logger.LogError("Membership returned no CompanyID for {CompanyGuid}", tenant.CompanyGuid);

                return Result.Failure<string>(
                    ErrorCodes.ExternalApiFailed,
                    "I could not look up your company in the expense system.");
            }

            return Result.Success(company.CompanyId);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Membership lookup failed for {CompanyGuid}", tenant.CompanyGuid);

            return Result.Failure<string>(
                ErrorCodes.ExternalApiFailed,
                "I could not look up your company in the expense system.");
        }
    }
}
