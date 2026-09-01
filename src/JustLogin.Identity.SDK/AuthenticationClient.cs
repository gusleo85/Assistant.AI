using JustLogin.Identity.SDK.DTO.ApiResponses.v1.Authentication;
using JustLogin.Identity.SDK.DTO.ApiResponses.v2.Membership;
using JustLogin.Identity.SDK.DTO.Models;
using JustLogin.Identity.SDK.Exceptions;
using JustLogin.Identity.SDK.Helpers;
using JustLogin.Identity.SDK.Interfaces;
using JustLogin.Identity.SDK.Responses;
using JustLogin.Identity.SDK.Startup.Configurations;
using JustLogin.Identity.SDK.SystemToken.Interfaces;
using JustLogin.Identity.SDK.ValueObjects;
using JustLogin.SDK.Core;
using JustLogin.SDK.Core.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;

namespace JustLogin.Identity.SDK;

public class AuthenticationClient : SdkClientBase<IAuthenticationClient, AuthenticationClient>, IAuthenticationClient
{
    private readonly ILogger<AuthenticationClient> _logger;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IdentityConfiguration _endpointConfiguration;
    private readonly ISingletonAuthenticationClient _singletonAuthenticationClient;
    private GetAuthenticationResponse _systemToken;
    private Dictionary<string, GetAuthenticationResponse> _companySystemTokens;
    private IAuthenticationClient internalService => this;
    public AuthenticationClient(ILogger<AuthenticationClient> logger
    , IHttpClientFactory httpClientFactory
    , IOptions<IdentityConfiguration> endpointConfigurationOptions
    , ISingletonAuthenticationClient singletonAuthenticationClient) : base(httpClientFactory)
    {
        _logger = logger;
        _httpClientFactory = httpClientFactory;
        _singletonAuthenticationClient = singletonAuthenticationClient;
        _endpointConfiguration = endpointConfigurationOptions.Value;

        _companySystemTokens = new Dictionary<string, GetAuthenticationResponse>();
        // _httpContextAccessor = httpContextAccessor;
    }

    async Task<GetAuthenticationResponse> IAuthenticationClient.GetCompanySystemToken(CompanyGuid companyGuid, CancellationToken cancellationToken)
    {
        var membershipCompanies = await GetCompany(companyGuid, cancellationToken);
        if (membershipCompanies.HttpStatusCode != HttpStatusCode.OK || membershipCompanies.Response is null || membershipCompanies.Response.Count() == 0)
        {
            throw new GenerateSystemTokenException($"For CompanyGuid: {companyGuid} " + membershipCompanies.Message);
        }
        var membershipCompany = membershipCompanies.Response.FirstOrDefault();

        // Get Token
        var identityFormCollection = new Dictionary<string, string>();
        identityFormCollection.Add("grant_type", "client_credentials");
        identityFormCollection.Add("CompanyID", membershipCompany.CompanyId);
        var tokenResponse = await internalService.GenerateToken(identityFormCollection, cancellationToken);

        if (tokenResponse.HttpStatusCode != HttpStatusCode.OK)
        {
            throw new GenerateSystemTokenException($"For CompanyGuid: {companyGuid} " + tokenResponse.Message);
        }

        // Save Token
        var companySystemToken = tokenResponse.Response.FirstOrDefault();

        return companySystemToken;

    }
    async Task<GetAuthenticationResponse> IAuthenticationClient.GetSystemToken(CancellationToken cancellationToken)
    {
        return await _singletonAuthenticationClient.GetSystemToken(cancellationToken);
    }
    async Task<SdkResponse<GetAuthenticationResponse>> IAuthenticationClient.GenerateExchangeToken(ExchangeTokenGuid exchangeTokenGuid, CancellationToken cancellationToken)
    {
        var url = _endpointConfiguration.TokenEndpoint;

        var httpClient = GetHttpClient();
        Dictionary<string, string> identityFormCollection = new();
        // TODO: Have a unique grant_type for exchange token
        identityFormCollection.Add("grant_type", "password");
        identityFormCollection.Add("username", " ");
        identityFormCollection.Add("password", " ");
        identityFormCollection.Add("ExchangeToken", exchangeTokenGuid.Value);
        // TODO: Add some form of validation rules for the exchange token using client_id
        identityFormCollection.Add("client_id", _endpointConfiguration.ClientId);
        identityFormCollection.Add("scope", _endpointConfiguration.Scope);
        var response = await httpClient.PostAsync(url, new FormUrlEncodedContent(identityFormCollection), cancellationToken);

        var results = new List<GetAuthenticationResponse>();
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError("TokenService: Token API {TokenEndpoint} Response is {StatusCode} Reason is {ErrorReason}", _endpointConfiguration.TokenEndpoint, response.StatusCode, response.ReasonPhrase);
            return SdkResponse<GetAuthenticationResponse>.Create(results, response.StatusCode, message: $"Token API {_endpointConfiguration.TokenEndpoint} Response is {response.StatusCode} Reason is {response.ReasonPhrase}");
        }

        var tokenResponse = await response.Content.ReadFromJsonAsync<AuthenticationResponse>(cancellationToken: cancellationToken);

        if (tokenResponse is null)
        {
            _logger.LogError("TokenService: Token API {TokenEndpoint} Response is {StatusCode} Reason is {ErrorReason}", _endpointConfiguration.TokenEndpoint, response.StatusCode, response.ReasonPhrase);
            return SdkResponse<GetAuthenticationResponse>.Create(results, response.StatusCode, message: $"Token API {_endpointConfiguration.TokenEndpoint} Response is {response.StatusCode} Reason is {response.ReasonPhrase}");
        }

        return SdkResponse<GetAuthenticationResponse>.Create(new List<GetAuthenticationResponse>() { GetAuthenticationResponse.Map(tokenResponse) });
    }
    async Task<SdkResponse<GetAuthenticationResponse>> IAuthenticationClient.GenerateToken(Dictionary<string, string> identityFormCollection, CancellationToken cancellationToken = default)
    {
        try
        {
            var url = _endpointConfiguration.TokenEndpoint;

            var httpClient = GetHttpClient();
            identityFormCollection.Add("client_id", _endpointConfiguration.ClientId);
            identityFormCollection.Add("client_secret", _endpointConfiguration.ClientSecret);
            identityFormCollection.Add("scope", _endpointConfiguration.Scope);
            var response = await httpClient.PostAsync(url, new FormUrlEncodedContent(identityFormCollection), cancellationToken);
            var results = new List<GetAuthenticationResponse>();

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError("TokenService: Token API {TokenEndpoint} Response is {StatusCode} Reason is {ErrorReason}", _endpointConfiguration.TokenEndpoint, response.StatusCode, response.ReasonPhrase);
                return SdkResponse<GetAuthenticationResponse>.Create(results, response.StatusCode, message: $"Token API {_endpointConfiguration.TokenEndpoint} Response is {response.StatusCode} Reason is {response.ReasonPhrase}");
            }

            // TODO: Rename local variabled to match the response type
            var tokenResponse = await response.Content.ReadFromJsonAsync<AuthenticationResponse>(cancellationToken: cancellationToken);
            if (tokenResponse is null)
            {
                // TODO: Return NotFound response
                return SdkResponse<GetAuthenticationResponse>.Create(results, HttpStatusCode.NotFound, message: "Company not found");
            }

            return SdkResponse<GetAuthenticationResponse>.Create(new List<GetAuthenticationResponse>() { GetAuthenticationResponse.Map(tokenResponse) });
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "API call {tokenEndpoint} from the Identidy Server {ErrorReason}", _endpointConfiguration.TokenEndpoint, exception.Message);
            throw new GenerateTokenErrorException(exception);
        }
    }
    private async Task<SdkResponse<GetMembershipCompanyResponse>> GetCompany(string companyGuid, CancellationToken cancellationToken = default)
    {
        var url = string.Format(_endpointConfiguration.MembershipCompanyEndpoint, companyGuid);

        var httpClient = GetHttpClient();

        var systemToken = await internalService.GetSystemToken(cancellationToken);
        httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(systemToken.TokenType, systemToken.AccessToken);

        var response = await httpClient.GetAsync(url, cancellationToken);
        var results = new List<GetMembershipCompanyResponse>();

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError("TokenService: Membership Company API {TokenEndpoint} Response is {StatusCode} Reason is {ErrorReason}", _endpointConfiguration.MembershipCompanyEndpoint, response.StatusCode, response.ReasonPhrase);
            return SdkResponse<GetMembershipCompanyResponse>.Create(results, response.StatusCode, message: $"Membership Company API {_endpointConfiguration.TokenEndpoint} Response is {response.StatusCode} Reason is {response.ReasonPhrase}");
        }

        // TODO: Rename local variabled to match the response type
        var company = await response.Content.ReadFromJsonAsync<MembershipCompanyResponse>(cancellationToken: cancellationToken);
        if (company is null)
        {
            // TODO: Return NotFound response
            return SdkResponse<GetMembershipCompanyResponse>.Create(new List<GetMembershipCompanyResponse>(), HttpStatusCode.NotFound, message: "Company not found");
        }

        return SdkResponse<GetMembershipCompanyResponse>.Create(new List<GetMembershipCompanyResponse>() { GetMembershipCompanyResponse.Map(company) });
    }
}
