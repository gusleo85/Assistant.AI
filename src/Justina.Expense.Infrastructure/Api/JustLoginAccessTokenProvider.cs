using System.Collections.Concurrent;
using System.Net;
using Justina.Core.Domain.Results;
using Justina.Expense.Application.Abstractions;
using JustLogin.Identity.SDK.Helpers;
using JustLogin.Identity.SDK.Interfaces;
using JustLogin.Identity.SDK.Responses;
using Microsoft.Extensions.Logging;

namespace Justina.Expense.Infrastructure.Api;

/// <summary>
/// Company tokens from the JustLogin identity server, through the vendored Identity SDK.
///
/// The SDK's own <c>GetCompanySystemToken</c> performs two round trips every time it is called — a
/// membership lookup for the company, then a token request — because its caching is commented out
/// upstream. Left alone that would be two extra calls per catalogue fetch and per submission. This class
/// caches the result per company instead, and is the only place that talks to the SDK.
///
/// It is also the boundary where the SDK's exceptions become results. The SDK throws
/// <c>GenerateSystemTokenException</c> for an ordinary bad response; Justina reserves exceptions for
/// defects, so a refusal is translated into an <see cref="Result"/> the user can be told about (§29).
/// </summary>
public sealed class JustLoginAccessTokenProvider(
    IAuthenticationClient authenticationClient,
    IJustLoginCompanyDirectory companies,
    ILogger<JustLoginAccessTokenProvider> logger)
    : IExpenseAccessTokenProvider
{
    /// <summary>
    /// Refresh this long before the token actually expires. A token that is valid when we check it and
    /// expired when the API reads it fails a submission for no reason; the SDK applies the same five
    /// minutes to its system token.
    /// </summary>
    private static readonly TimeSpan RefreshMargin = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Keyed by company. A process serves several companies, and one company's token is useless to
    /// another — mixing them would file an expense against the wrong organization.
    /// </summary>
    private readonly ConcurrentDictionary<string, CachedToken> _tokens = new(StringComparer.Ordinal);

    public async Task<Result<string>> GetAsync(ExpenseTenant tenant, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tenant);

        var companyGuid = tenant.CompanyGuid;

        if (_tokens.TryGetValue(companyGuid, out var cached) && cached.IsUsable(DateTimeOffset.UtcNow))
        {
            return Result.Success(cached.Value);
        }

        // Which company, in the identity server's own terms. Split out from the token request because
        // the two halves are believed independently: identity is live today, while the membership
        // lookup can be served from a fixture whose answer for our one dev company is a constant.
        var companyId = await companies.GetCompanyIdAsync(tenant, cancellationToken).ConfigureAwait(false);

        if (companyId.IsFailure)
        {
            return Result.Failure<string>(companyId.Error);
        }

        try
        {
            // Exactly the request the SDK's GetCompanySystemToken makes after its own membership lookup:
            // client credentials plus CompanyID. client_id, client_secret and scope are added by the SDK.
            var response = await authenticationClient
                .GenerateToken(
                    new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["grant_type"] = "client_credentials",
                        ["CompanyID"] = companyId.Value,
                    },
                    cancellationToken)
                .ConfigureAwait(false);

            var token = response.Response?.FirstOrDefault();

            if (response.HttpStatusCode != HttpStatusCode.OK
                || token is null
                || string.IsNullOrWhiteSpace(token.AccessToken))
            {
                logger.LogError("Identity returned no access token for company {CompanyGuid}", companyGuid);

                return Result.Failure<string>(
                    ErrorCodes.ExternalApiFailed,
                    "I could not sign in to the expense system just now.");
            }

            // TokenExpire is the JWT's own exp claim, read by the SDK when it mapped the response. If it
            // is missing we still use the token, but cache nothing: better an extra round trip than a
            // credential we cannot tell the age of.
            var expiresAt = token.TokenExpire is { } unixSeconds
                ? DateTimeOffset.FromUnixTimeSeconds(unixSeconds)
                : (DateTimeOffset?)null;

            if (expiresAt is { } expiry)
            {
                _tokens[companyGuid] = new CachedToken(token.AccessToken, expiry);
            }

            Describe(token, companyId.Value, expiresAt);

            return Result.Success(token.AccessToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            // The SDK throws for a failed token request, a failed membership lookup and a transport
            // fault alike, so everything is caught here and reported as the same refusal. The detail
            // goes to the log; the user gets a sentence. Nothing from the exception reaches them,
            // because identity errors quote request contents.
            logger.LogError(
                exception,
                "Could not obtain a company token for {CompanyGuid}",
                companyGuid);

            return Result.Failure<string>(
                ErrorCodes.ExternalApiFailed,
                "I could not sign in to the expense system just now.");
        }
    }

    /// <summary>
    /// Says what arrived, without ever writing the token itself.
    ///
    /// It exists for one failure that is otherwise invisible: send the wrong field name — the GUID
    /// instead of the CompanyID, or <c>CompanyId</c> where the server wants <c>CompanyID</c> — and the
    /// identity server issues a perfectly valid token with no company claims on it. Nothing fails until
    /// the Expense API refuses the call, by which point the cause is three services away. The claim
    /// names are the evidence, so they are logged and the credential is not.
    /// </summary>
    private void Describe(GetAuthenticationResponse token, string companyId, DateTimeOffset? expiresAt)
    {
        string? company = null;

        try
        {
            company = token.GetJustLoginToken()?.CompanyGUID;
        }
        catch (Exception exception)
        {
            // Reading claims is diagnostics; a token we cannot parse is still a token the API may accept.
            logger.LogDebug(exception, "Could not read the claims of the company token");
        }

        if (string.IsNullOrWhiteSpace(company))
        {
            logger.LogWarning(
                "Identity issued a token for CompanyID {CompanyId} with no CompanyGUID claim. It will be " +
                "sent, but the Expense API is likely to refuse it — check the CompanyID and its spelling",
                companyId);

            return;
        }

        logger.LogInformation(
            "Company token for CompanyID {CompanyId} carries CompanyGUID {CompanyGuid}, valid until {Expiry}",
            companyId,
            company,
            expiresAt);
    }

    private sealed record CachedToken(string Value, DateTimeOffset ExpiresAt)
    {
        public bool IsUsable(DateTimeOffset now) => now < ExpiresAt - RefreshMargin;
    }
}
