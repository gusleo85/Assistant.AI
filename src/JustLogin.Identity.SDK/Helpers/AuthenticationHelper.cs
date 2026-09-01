using JustLogin.Identity.SDK.DTO.Models;
using JustLogin.Identity.SDK.Responses;
using System.IdentityModel.Tokens.Jwt;

namespace JustLogin.Identity.SDK.Helpers;
public static class AuthenticationHelper
{
    public static JwtSecurityTokenHandler _handler = new JwtSecurityTokenHandler();
    public static bool IsNotExpiredToken(this GetAuthenticationResponse token)
    {
        if (token is null)
        {
            return false;
        }

        var todayUnixTime = DateTimeOffset.Now.ToUnixTimeSeconds();
        var tokenExpireDateTime = DateTimeOffset.FromUnixTimeSeconds(token.TokenExpire.Value);
        var tokenExpire = tokenExpireDateTime.AddMinutes(-5).ToUnixTimeSeconds(); // 5 minutes
        var isExpireSystemToken = tokenExpire <= todayUnixTime;

        return !(isExpireSystemToken);
    }
    public static JustLoginToken GetJustLoginToken(this GetAuthenticationResponse token)
    {
        var jwtToken = _handler.ReadToken(token.AccessToken) as JwtSecurityToken;
        return jwtToken.GetJWTTokenData();
    }
}