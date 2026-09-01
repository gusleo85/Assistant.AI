using JustLogin.Identity.SDK.Startup.Configurations;

namespace JustLogin.Identity.SDK.Startup.Options;
public class IdentitySdkOptions
{
    public IdentityConfiguration Configurations { get; set; } = new IdentityConfiguration();
}