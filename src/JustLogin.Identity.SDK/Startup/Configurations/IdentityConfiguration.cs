namespace JustLogin.Identity.SDK.Startup.Configurations;
public class IdentityConfiguration
{
    public bool IsNotInitialized
    {
        get
        {
            return string.IsNullOrEmpty(TokenEndpoint) || string.IsNullOrEmpty(ClientId) || string.IsNullOrEmpty(ClientSecret) || string.IsNullOrEmpty(Scope) || string.IsNullOrEmpty(MembershipCompanyEndpoint);
        }
    }
    public string TokenEndpoint { get; set; }
    public string ClientId { get; internal set; }
    public string ClientSecret { get; internal set; }
    public string Scope { get; internal set; }
    public string MembershipCompanyEndpoint { get; set; }
}