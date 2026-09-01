using JustLogin.Identity.SDK.DTO.Models;
using System.IdentityModel.Tokens.Jwt;

namespace JustLogin.Identity.SDK.Helpers;
public static class TokenDecoderHelper
{
    public static JustLoginToken GetJWTTokenData(this JwtSecurityToken jwtToken) 
    {
        JustLoginToken jlToken = new JustLoginToken(); 
        string id = "";   
        var hasJTI = jwtToken.Claims.Any(c=>c.Type == "jti");
        if(hasJTI)
        {
            id = jwtToken.Claims.First(claim => claim.Type == "jti").Value; 
        }

        var hasUnique = jwtToken.Claims.Any(c=>c.Type == "sub");
        if(hasUnique)
        {
            id = jwtToken.Claims.First(claim => claim.Type == "sub").Value; 
        }
            
        //string id = jwtToken.Claims.First(claim => claim.Type == "unique_name").Value;
        string issuer = jwtToken.Claims.First(claim => claim.Type == "iss").Value;
        string audience = jwtToken.Claims.First(claim => claim.Type == "aud").Value;
        string tokenExpire = jwtToken.Claims.First(claim => claim.Type == "exp").Value;

        var hasCompanyGuid = jwtToken.Claims.Any(c => c.Type == "CompanyGUID");
        string companyGUID = hasCompanyGuid ? jwtToken.Claims.First(claim => claim.Type == "CompanyGUID").Value : "";

        var hasCompanyID = jwtToken.Claims.Any(c => c.Type == "CompanyID");
        string companyID = hasCompanyID ? jwtToken.Claims.First(claim => claim.Type == "CompanyID").Value : "";

        var hasCompanyName = jwtToken.Claims.Any(c => c.Type == "CompanyName");
        string companyName = hasCompanyName ? jwtToken.Claims.First(claim => claim.Type == "CompanyName").Value : "";

        // Username is UserID in Token from Identity Server
        var hasUserName = jwtToken.Claims.Any(c => c.Type == "Username");
        string userID = hasUserName ? jwtToken.Claims.First(claim => claim.Type == "Username").Value : "";
        
        var hasUserGuid = jwtToken.Claims.Any(c => c.Type == "UserGUID");
        string userGUID = hasUserGuid ? jwtToken.Claims.First(claim => claim.Type == "UserGUID").Value : "";

        var hasName = jwtToken.Claims.Any(c => c.Type == "name");
        string fullName = hasName ? jwtToken.Claims.First(claim => claim.Type == "name").Value : "";

        var hasWorkEmail = jwtToken.Claims.Any(c => c.Type == "WorkEmail");
        string WorkEmail = hasWorkEmail ? jwtToken.Claims.First(claim => claim.Type == "WorkEmail").Value : "";

        var hasRoles = jwtToken.Claims.Any(c=>c.Type == "role");
        if(hasRoles)
        {
            var roleClaims = jwtToken.Claims.Where(claim => claim.Type == "role"); 
            foreach(var c in roleClaims)
            {
                jlToken.Roles.Add(c.Value);
            }                
        }

        // Convert Timespan to DateTime
        // DateTimeOffset dateTimeOffset = DateTimeOffset.FromUnixTimeSeconds(long.Parse(tokenExpire));
        // DateTime tokenExpireDate = dateTimeOffset.UtcDateTime;
        
        jlToken.TokenData.ID = id;
        jlToken.TokenData.Issuer = issuer;
        jlToken.TokenData.Audience = audience;
        jlToken.TokenData.TokenExpire = tokenExpire;
        jlToken.CompanyGUID= companyGUID;
        jlToken.CompanyID = companyID;
        jlToken.CompanyName = companyName;
        jlToken.UserGUID= userGUID;
        jlToken.Username = userID;
        jlToken.FullName = fullName;
        jlToken.WorkEmail = WorkEmail;

        return jlToken;
    }
}