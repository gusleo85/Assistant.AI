using Justina.Core.Domain.Results;
using Justina.Expense.Application.Abstractions;
using Justina.Expense.Infrastructure.Api;
using JustLogin.Identity.SDK.Exceptions;
using JustLogin.Identity.SDK.Interfaces;
using JustLogin.Identity.SDK.Responses;
using JustLogin.Identity.SDK.ValueObjects;
using JustLogin.SDK.Core.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace Justina.IntegrationTests;

/// <summary>
/// The seam between the vendored SDK and Justina. Two things are being pinned: that a token is fetched
/// once per company rather than on every call, and that an identity failure arrives as a refusal instead
/// of an exception escaping into a command handler.
/// </summary>
public class JustLoginAccessTokenProviderTests
{
    private static readonly ExpenseTenant Tenant = new(
        Guid.Parse("278c65ef-0000-4000-8000-000000000001"),
        "COMPANY-1",
        Guid.Parse("278c65ef-0000-4000-8000-000000000002"));

    private static readonly ExpenseTenant OtherTenant = new(
        Guid.Parse("999c65ef-0000-4000-8000-000000000001"),
        "COMPANY-2",
        Guid.Parse("999c65ef-0000-4000-8000-000000000002"));

    [Fact]
    public async Task A_company_token_is_fetched_once_and_then_reused()
    {
        var client = new FakeAuthenticationClient(TokenExpiringIn(TimeSpan.FromHours(1)));
        var provider = new JustLoginAccessTokenProvider(client, NullLogger<JustLoginAccessTokenProvider>.Instance);

        var first = await provider.GetAsync(Tenant, default);
        var second = await provider.GetAsync(Tenant, default);

        first.Value.ShouldBe("token-1");
        second.Value.ShouldBe("token-1");

        // Upstream's caching is commented out, so without ours this would be two membership lookups and
        // two token requests for one conversation.
        client.Calls.ShouldBe(1);
    }

    [Fact]
    public async Task Each_company_gets_its_own_token()
    {
        var client = new FakeAuthenticationClient(TokenExpiringIn(TimeSpan.FromHours(1)));
        var provider = new JustLoginAccessTokenProvider(client, NullLogger<JustLoginAccessTokenProvider>.Instance);

        await provider.GetAsync(Tenant, default);
        await provider.GetAsync(OtherTenant, default);

        // One company's token filed against another company's expense is the failure this guards.
        client.Calls.ShouldBe(2);
        client.RequestedCompanies.ShouldBe([Tenant.CompanyGuid, OtherTenant.CompanyGuid]);
    }

    [Fact]
    public async Task A_token_inside_the_refresh_margin_is_replaced()
    {
        // Valid, but only for another two minutes: valid when we check and expired when the API reads it
        // is a submission that fails for no reason the user can act on.
        var client = new FakeAuthenticationClient(TokenExpiringIn(TimeSpan.FromMinutes(2)));
        var provider = new JustLoginAccessTokenProvider(client, NullLogger<JustLoginAccessTokenProvider>.Instance);

        await provider.GetAsync(Tenant, default);
        await provider.GetAsync(Tenant, default);

        client.Calls.ShouldBe(2);
    }

    [Fact]
    public async Task An_identity_failure_is_a_refusal_not_an_exception()
    {
        var client = new FakeAuthenticationClient(_ => throw new GenerateSystemTokenException("upstream said no"));
        var provider = new JustLoginAccessTokenProvider(client, NullLogger<JustLoginAccessTokenProvider>.Instance);

        var result = await provider.GetAsync(Tenant, default);

        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe(ErrorCodes.ExternalApiFailed);

        // The SDK's message quotes the request it made; the user gets ours.
        result.Error.Message.ShouldNotContain("upstream said no");
    }

    [Fact]
    public async Task A_token_with_no_expiry_is_used_but_never_cached()
    {
        var client = new FakeAuthenticationClient(_ => new GetAuthenticationResponse
        {
            TokenType = "Bearer",
            AccessToken = "token-1",
            TokenExpire = null,
        });

        var provider = new JustLoginAccessTokenProvider(client, NullLogger<JustLoginAccessTokenProvider>.Instance);

        (await provider.GetAsync(Tenant, default)).Value.ShouldBe("token-1");
        await provider.GetAsync(Tenant, default);

        // An extra round trip beats holding a credential whose age we cannot tell.
        client.Calls.ShouldBe(2);
    }

    [Fact]
    public async Task An_empty_access_token_is_refused_rather_than_sent()
    {
        var client = new FakeAuthenticationClient(_ => new GetAuthenticationResponse
        {
            TokenType = "Bearer",
            AccessToken = "   ",
            TokenExpire = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds(),
        });

        var provider = new JustLoginAccessTokenProvider(client, NullLogger<JustLoginAccessTokenProvider>.Instance);
        var result = await provider.GetAsync(Tenant, default);

        // Sending it would produce a bare "Bearer" header and an opaque 401 that reads like an outage.
        result.IsFailure.ShouldBeTrue();
    }

    private static Func<string, GetAuthenticationResponse> TokenExpiringIn(TimeSpan lifetime)
    {
        var issued = 0;

        return _ => new GetAuthenticationResponse
        {
            TokenType = "Bearer",
            AccessToken = $"token-{++issued}",
            TokenExpire = DateTimeOffset.UtcNow.Add(lifetime).ToUnixTimeSeconds(),
        };
    }

    /// <summary>
    /// Stands in for the SDK's client. Only <c>GetCompanySystemToken</c> is exercised; the rest of the
    /// interface exists because the SDK declares it.
    /// </summary>
    private sealed class FakeAuthenticationClient(Func<string, GetAuthenticationResponse> respond)
        : IAuthenticationClient
    {
        public int Calls { get; private set; }

        public List<string> RequestedCompanies { get; } = [];

        public Task<GetAuthenticationResponse> GetCompanySystemToken(
            CompanyGuid companyGuid,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            RequestedCompanies.Add(companyGuid.Value);

            return Task.FromResult(respond(companyGuid.Value));
        }

        /// <summary>Part of the SDK's base contract; the provider never calls it.</summary>
        public IAuthenticationClient SetToken(string token) => this;

        public Task<GetAuthenticationResponse> GetSystemToken(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<SdkResponse<GetAuthenticationResponse>> GenerateToken(
            Dictionary<string, string> identityFormCollection,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<SdkResponse<GetAuthenticationResponse>> GenerateExchangeToken(
            ExchangeTokenGuid exchangeTokenGuid,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
