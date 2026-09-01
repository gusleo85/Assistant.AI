using JustLogin.Identity.SDK.Interfaces;
using JustLogin.Identity.SDK.Startup.Configurations;
using JustLogin.Identity.SDK.Startup.Helpers;
using JustLogin.Identity.SDK.Startup.Options;
using JustLogin.Identity.SDK.SystemToken;
using JustLogin.Identity.SDK.SystemToken.Interfaces;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace JustLogin.Identity.SDK.Startup;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddIdentitySdk(this IServiceCollection services, IConfiguration configuration)
    {
        var identityConfiguration = configuration.GetIdentityConfiguration();

        return Register(services, identityConfiguration);
    }
    
    public static IServiceCollection AddIdentitySdk(this IServiceCollection services, Action<IdentitySdkOptions> options)
    {
        var identitySdkOptions = new IdentitySdkOptions();
        options.Invoke(identitySdkOptions);

        if (identitySdkOptions.Configurations == null || identitySdkOptions.Configurations.IsNotInitialized)
        {
            // TODO: Create correct exception
            throw new ArgumentNullException(nameof(identitySdkOptions.Configurations));
        }
        
        return Register(services, identitySdkOptions.Configurations);
    }
    
    private static IServiceCollection Register(this IServiceCollection services, IdentityConfiguration configuration)
    {
        services.AddSingleton(Microsoft.Extensions.Options.Options.Create(configuration));
        
        services.AddSingleton<ISingletonAuthenticationClient, SingletonAuthenticationClient>();
        services.AddTransient<IAuthenticationClient, AuthenticationClient>();
        
        return services;
    }
    public static IApplicationBuilder GenerateSystemTokenOnStartup(this IApplicationBuilder app)
    {
        var authenticationClient = app.ApplicationServices.GetService<IAuthenticationClient>();
        var systemToken = authenticationClient.GetSystemToken().Result;
        return app;
    }
}