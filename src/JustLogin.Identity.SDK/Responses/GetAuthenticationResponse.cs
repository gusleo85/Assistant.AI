using JustLogin.Identity.SDK.DTO.ApiResponses.v1.Authentication;
using JustLogin.Identity.SDK.Helpers;

namespace JustLogin.Identity.SDK.Responses;
public class GetAuthenticationResponse
{
    public string? TokenType { get; set; }
    public string? AccessToken { get; set; }
    public int? ExpiresIn { get; set; }
    public long? TokenExpire { get; set; }
    public static GetAuthenticationResponse Map(AuthenticationResponse source)
    {
        var response = new GetAuthenticationResponse
        {
            TokenType = source.TokenType,
            AccessToken = string.IsNullOrWhiteSpace(source.AccessToken) ? null : source.AccessToken.Trim(),
            ExpiresIn = source.ExpiresIn == 0 ? null : source.ExpiresIn
        };

        var token = response.GetJustLoginToken();
        response.TokenExpire = long.Parse(token.TokenData.TokenExpire);

        return response;
    }
}