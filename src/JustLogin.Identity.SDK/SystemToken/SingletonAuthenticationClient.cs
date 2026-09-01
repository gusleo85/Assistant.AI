using JustLogin.Identity.SDK.DTO.ApiResponses.v1.Authentication;
using JustLogin.Identity.SDK.Exceptions;
using JustLogin.Identity.SDK.Helpers;
using JustLogin.Identity.SDK.Responses;
using JustLogin.Identity.SDK.Startup.Configurations;
using JustLogin.Identity.SDK.SystemToken.Interfaces;
using JustLogin.SDK.Core;
using JustLogin.SDK.Core.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Net;
using System.Net.Http.Json;

namespace JustLogin.Identity.SDK.SystemToken;

public class SingletonAuthenticationClient : SdkClientBase<ISingletonAuthenticationClient, SingletonAuthenticationClient>, ISingletonAuthenticationClient
{
    private readonly ILogger<SingletonAuthenticationClient> _logger;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IdentityConfiguration _endpointConfiguration;
    private GetAuthenticationResponse _systemToken;
    public SingletonAuthenticationClient(ILogger<SingletonAuthenticationClient> logger
    , IHttpClientFactory httpClientFactory
    , IOptions<IdentityConfiguration> endpointConfigurationOptions) : base(httpClientFactory)
    {
        _logger = logger;
        _httpClientFactory = httpClientFactory;
        _endpointConfiguration = endpointConfigurationOptions.Value;
    }
    async Task<GetAuthenticationResponse> ISingletonAuthenticationClient.GetSystemToken(CancellationToken cancellationToken)
    {
        if (IsNotExpiredSystemToken())
        {
            return _systemToken;
        }

        var identityFormCollection = new Dictionary<string, string>();
        var tokenResponse = await GenerateToken(cancellationToken);

        if (tokenResponse.HttpStatusCode != HttpStatusCode.OK)
        {
            throw new GenerateSystemTokenException(tokenResponse.Message);
        }

        _systemToken = tokenResponse.Response.FirstOrDefault();

        return _systemToken;
    }
    private bool IsNotExpiredSystemToken()
    {
        return _systemToken.IsNotExpiredToken();
    }
    private async Task<SdkResponse<GetAuthenticationResponse>> GenerateToken(CancellationToken cancellationToken = default)
    {
        try
        {
            var url = _endpointConfiguration.TokenEndpoint;

            var httpClient = GetHttpClient();
            var identityFormCollection = new Dictionary<string, string>();
            identityFormCollection.Add("client_id", _endpointConfiguration.ClientId);
            identityFormCollection.Add("client_secret", _endpointConfiguration.ClientSecret);
            identityFormCollection.Add("grant_type", "client_credentials");
            identityFormCollection.Add("scope", _endpointConfiguration.Scope);
            // VENDOR DIVERGENCE: upstream logs the whole form here at Information level, which writes
            // client_secret to every sink. Do not restore it. Justina scrubs credentials deliberately
            // (SecretScrubber, the OTel url.full redaction, RedactLoggedHeaders) and one destructured
            // dictionary would defeat all of it. The endpoint alone is enough to debug with.
            _logger.LogInformation("TokenService: requesting a system token from {TokenEndpoint}", _endpointConfiguration.TokenEndpoint);
            var response = await httpClient.PostAsync(url, new FormUrlEncodedContent(identityFormCollection), cancellationToken);

            var results = new List<GetAuthenticationResponse>();
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError("TokenService: Token API {TokenEndpoint} Response is {StatusCode} Reason is {ErrorReason}", _endpointConfiguration.TokenEndpoint, response.StatusCode, response.ReasonPhrase);
                return SdkResponse<GetAuthenticationResponse>.Create(results, response.StatusCode, message: $"Token API {_endpointConfiguration.TokenEndpoint} Response is {response.StatusCode} Reason is {response.ReasonPhrase}");
            }

            // TODO: Rename local variabled to match the response type
            var company = await response.Content.ReadFromJsonAsync<AuthenticationResponse>(cancellationToken: cancellationToken);
            if (company is null)
            {
                // TODO: Return NotFound response
                return SdkResponse<GetAuthenticationResponse>.Create(results, HttpStatusCode.NotFound, message: "Company not found");
            }

            return SdkResponse<GetAuthenticationResponse>.Create(new List<GetAuthenticationResponse>() { GetAuthenticationResponse.Map(company) });
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "API call {tokenEndpoint} from the Identidy Server {ErrorReason}", _endpointConfiguration.TokenEndpoint, exception.Message);
            throw new GenerateTokenErrorException(exception);
        }
    }
}
