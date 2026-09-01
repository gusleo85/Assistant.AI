using System.Net;
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
/// The seam between the vendored SDK and Justina. Three things are pinned here: that a company token is
/// fetched once per company rather than per call, that the token request carries the CompanyID the
/// identity server expects rather than the company GUID Justina uses internally, and that an identity
/// failure arrives as a refusal instead of an exception escaping into a command handler.
/// </summary>
public class JustLoginAccessTokenProviderTests
{
    private static readonly ExpenseTenant Tenant = new(
        Guid.Parse("1ba47eac-7ae7-4270-a3b8-a935f30c53ee"),
        "khinco",
        Guid.Parse("4b07c8bf-dda4-40b7-8042-ceaea8ed3342"));

    private static readonly ExpenseTenant OtherTenant = new(
        Guid.Parse("999c65ef-0000-4000-8000-000000000001"),
        "other-co",
        Guid.Parse("999c65ef-0000-4000-8000-000000000002"));

    [Fact]
    public async Task A_company_token_is_fetched_once_and_then_reused()
    {
        var client = new FakeAuthenticationClient(TokenExpiringIn(TimeSpan.FromHours(1)));
        var provider = Provider(client);

        var first = await provider.GetAsync(Tenant, default);
        var second = await provider.GetAsync(Tenant, default);

        first.Value.ShouldBe("token-1");
        second.Value.ShouldBe("token-1");

        // Upstream's caching is commented out, so without ours this would be a membership lookup and a
        // token request for every catalogue read and every submission.
        client.Calls.ShouldBe(1);
    }

    [Fact]
    public async Task The_token_request_carries_the_company_identifier_not_the_guid()
    {
        // The distinction the whole membership lookup exists for: the identity server accepts CompanyID,
        // and a GUID sent in its place comes back as a token with no company claims — accepted here,
        // rejected much later by the Expense API in a way that reads like an outage.
        var client = new FakeAuthenticationClient(TokenExpiringIn(TimeSpan.FromHours(1)));

        await Provider(client).GetAsync(Tenant, default);

        client.LastForm.ShouldNotBeNull();
        client.LastForm!["CompanyID"].ShouldBe("company-id-for-khinco");
        client.LastForm["grant_type"].ShouldBe("client_credentials");
        client.LastForm.ShouldNotContainKey("client_secret", "the SDK adds credentials; we must not");
    }

    [Fact]
    public async Task Each_company_gets_its_own_token()
    {
        var client = new FakeAuthenticationClient(TokenExpiringIn(TimeSpan.FromHours(1)));
        var provider = Provider(client);

        await provider.GetAsync(Tenant, default);
        await provider.GetAsync(OtherTenant, default);

        // One company's token filed against another company's expense is the failure this guards.
        client.Calls.ShouldBe(2);
    }

    [Fact]
    public async Task A_token_inside_the_refresh_margin_is_replaced()
    {
        // Valid, but only for another two minutes: valid when we check it and expired when the API reads
        // it is a submission that fails for no reason the user can act on.
        var client = new FakeAuthenticationClient(TokenExpiringIn(TimeSpan.FromMinutes(2)));
        var provider = Provider(client);

        await provider.GetAsync(Tenant, default);
        await provider.GetAsync(Tenant, default);

        client.Calls.ShouldBe(2);
    }

    [Fact]
    public async Task An_unknown_company_never_reaches_the_identity_server()
    {
        var client = new FakeAuthenticationClient(TokenExpiringIn(TimeSpan.FromHours(1)));
        var provider = new JustLoginAccessTokenProvider(
            client,
            new FakeCompanyDirectory(null),
            NullLogger<JustLoginAccessTokenProvider>.Instance);

        var result = await provider.GetAsync(Tenant, default);

        result.IsFailure.ShouldBeTrue();
        client.Calls.ShouldBe(0);
    }

    [Fact]
    public async Task An_identity_failure_is_a_refusal_not_an_exception()
    {
        var client = new FakeAuthenticationClient(_ => throw new GenerateSystemTokenException("upstream said no"));

        var result = await Provider(client).GetAsync(Tenant, default);

        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe(ErrorCodes.ExternalApiFailed);

        // The SDK's message quotes the request it made; the user gets ours.
        result.Error.Message.ShouldNotContain("upstream said no");
    }

    [Fact]
    public async Task A_non_success_status_is_refused_even_with_a_body()
    {
        var client = new FakeAuthenticationClient(
            _ => new GetAuthenticationResponse { AccessToken = "token-1", TokenExpire = Expiry(TimeSpan.FromHours(1)) },
            HttpStatusCode.Unauthorized);

        (await Provider(client).GetAsync(Tenant, default)).IsFailure.ShouldBeTrue();
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

        var provider = Provider(client);

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
            TokenExpire = Expiry(TimeSpan.FromHours(1)),
        });

        // Sending it would produce a bare "Bearer" header and an opaque 401 that reads like an outage.
        (await Provider(client).GetAsync(Tenant, default)).IsFailure.ShouldBeTrue();
    }

    private static JustLoginAccessTokenProvider Provider(FakeAuthenticationClient client) =>
        new(client,
            new FakeCompanyDirectory("company-id-for-khinco"),
            NullLogger<JustLoginAccessTokenProvider>.Instance);

    private static long Expiry(TimeSpan lifetime) => DateTimeOffset.UtcNow.Add(lifetime).ToUnixTimeSeconds();

    private static Func<Dictionary<string, string>, GetAuthenticationResponse> TokenExpiringIn(TimeSpan lifetime)
    {
        var issued = 0;

        return _ => new GetAuthenticationResponse
        {
            TokenType = "Bearer",
            AccessToken = $"token-{++issued}",
            TokenExpire = Expiry(lifetime),
        };
    }

    private sealed class FakeCompanyDirectory(string? companyId) : IJustLoginCompanyDirectory
    {
        public Task<Result<string>> GetCompanyIdAsync(ExpenseTenant tenant, CancellationToken cancellationToken) =>
            Task.FromResult(companyId is null
                ? Result.Failure<string>(ErrorCodes.NotAvailable, "Unknown company.")
                : Result.Success(companyId));
    }

    /// <summary>
    /// Stands in for the SDK's client. Only <c>GenerateToken</c> is exercised; the rest of the interface
    /// exists because the SDK declares it.
    /// </summary>
    private sealed class FakeAuthenticationClient(
        Func<Dictionary<string, string>, GetAuthenticationResponse> respond,
        HttpStatusCode status = HttpStatusCode.OK)
        : IAuthenticationClient
    {
        public int Calls { get; private set; }

        public Dictionary<string, string>? LastForm { get; private set; }

        public Task<SdkResponse<GetAuthenticationResponse>> GenerateToken(
            Dictionary<string, string> identityFormCollection,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            LastForm = new Dictionary<string, string>(identityFormCollection, StringComparer.Ordinal);

            return Task.FromResult(SdkResponse<GetAuthenticationResponse>.Create(
                [respond(identityFormCollection)],
                status));
        }

        /// <summary>Part of the SDK's base contract; the provider never calls it.</summary>
        public IAuthenticationClient SetToken(string token) => this;

        public Task<GetAuthenticationResponse> GetCompanySystemToken(
            CompanyGuid companyGuid,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<GetAuthenticationResponse> GetSystemToken(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<SdkResponse<GetAuthenticationResponse>> GenerateExchangeToken(
            ExchangeTokenGuid exchangeTokenGuid,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
