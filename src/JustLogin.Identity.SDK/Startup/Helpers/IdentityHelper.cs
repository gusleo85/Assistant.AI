using Justlogin.Configurations;
using JustLogin.Identity.SDK.Startup.Configurations;
using Microsoft.Extensions.Configuration;

namespace JustLogin.Identity.SDK.Startup.Helpers;
public static class IdentityHelper
{
    public static IdentityConfiguration GetIdentityConfiguration(this IConfiguration configuration)
    {
        var tokenEndpoint = configuration.GetValue("IdentitySDK:TokenEndpoint");
        if (string.IsNullOrEmpty(tokenEndpoint))
        {
            tokenEndpoint = "v1/auth/connect/token";
        }
        var membershipCompanyEndpoint = configuration.GetValue("IdentitySDK:MembershipCompanyEndpoint");
        if (string.IsNullOrEmpty(membershipCompanyEndpoint))
        {
            membershipCompanyEndpoint = "membership/v2/companies/{0}";
        }
        var clientId = configuration.GetValue("IdentitySDK:ClientID");
        var clientSecret = configuration.GetValue("IdentitySDK:ClientSecret");
        var scope = configuration.GetValue("IdentitySDK:Scope");
        return new IdentityConfiguration
        {
            TokenEndpoint = tokenEndpoint,
            ClientId = clientId,
            ClientSecret = clientSecret,
            Scope = scope,
            MembershipCompanyEndpoint = membershipCompanyEndpoint
        };
    }
}