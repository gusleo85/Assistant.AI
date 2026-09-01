using JustLogin.Identity.SDK.Responses;
using JustLogin.Identity.SDK.ValueObjects;
using JustLogin.SDK.Core.Interfaces;
using JustLogin.SDK.Core.Models;

namespace JustLogin.Identity.SDK.SystemToken.Interfaces;

public interface ISingletonAuthenticationClient : ISdkClientBase<ISingletonAuthenticationClient>
{
    Task<GetAuthenticationResponse> GetSystemToken(CancellationToken cancellationToken = default);
}
