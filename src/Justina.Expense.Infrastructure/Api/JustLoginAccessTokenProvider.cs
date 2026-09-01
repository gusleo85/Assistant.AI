using System.Collections.Concurrent;
using Justina.Core.Domain.Results;
using Justina.Expense.Application.Abstractions;
using JustLogin.Identity.SDK.Interfaces;
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

        try
        {
            var token = await authenticationClient
                .GetCompanySystemToken(companyGuid, cancellationToken)
                .ConfigureAwait(false);

            if (token is null || string.IsNullOrWhiteSpace(token.AccessToken))
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

    private sealed record CachedToken(string Value, DateTimeOffset ExpiresAt)
    {
        public bool IsUsable(DateTimeOffset now) => now < ExpiresAt - RefreshMargin;
    }
}
