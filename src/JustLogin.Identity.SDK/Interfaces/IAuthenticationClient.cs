using JustLogin.Identity.SDK.Responses;
using JustLogin.Identity.SDK.ValueObjects;
using JustLogin.SDK.Core.Interfaces;
using JustLogin.SDK.Core.Models;

namespace JustLogin.Identity.SDK.Interfaces;

public interface IAuthenticationClient : ISdkClientBase<IAuthenticationClient>
{
    Task<GetAuthenticationResponse> GetSystemToken(CancellationToken cancellationToken = default);
    Task<GetAuthenticationResponse> GetCompanySystemToken(CompanyGuid companyGuid, CancellationToken cancellationToken = default);
    Task<SdkResponse<GetAuthenticationResponse>> GenerateToken(Dictionary<string, string> identityFormCollection, CancellationToken cancellationToken = default);
    Task<SdkResponse<GetAuthenticationResponse>> GenerateExchangeToken(ExchangeTokenGuid excchangeTokenGuid, CancellationToken cancellationToken = default);
}
