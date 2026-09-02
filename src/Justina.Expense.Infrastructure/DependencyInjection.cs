using Justina.Core.Infrastructure.Persistence;
using Justina.Expense.Application.Abstractions;
using Justina.Expense.Infrastructure.Api;
using Justina.Expense.Infrastructure.Persistence;
using Justlogin.Configurations.HttpClient.Startup.Extensions;
using JustLogin.Identity.SDK.Startup;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Justina.Expense.Infrastructure;

/// <summary>
/// Wires the Expense infrastructure. Three seams — catalogue, tenant, submission — each choose a mock or
/// a real implementation from configuration alone:
///
/// <code>
/// ExpenseApi__Mode=Stub                 all three mocked (the default)
/// ExpenseApi__Mode=Live                 all three against the Expense API
/// ExpenseApi__CatalogueMode=Live        one seam at a time, the rest follow Mode
/// </code>
///
/// Nothing above this class knows which is in use: the handlers depend on
/// <see cref="IExpenseCatalogue"/>, <see cref="IExpenseTenantResolver"/> and
/// <see cref="IExpenseApiClient"/>, and the swap is a restart with different configuration.
/// </summary>
public static class ExpenseInfrastructureServiceCollectionExtensions
{
    public static IServiceCollection AddExpenseInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var section = configuration.GetSection(ExpenseApiOptions.SectionName);
        services.Configure<ExpenseApiOptions>(section);

        var options = section.Get<ExpenseApiOptions>() ?? new ExpenseApiOptions();

        services.AddSingleton<IModelConfiguration, ExpenseModelConfiguration>();
        services.AddScoped<IReceiptRepository, ReceiptRepository>();
        services.TryAddSingleton<IMemoryCache, MemoryCache>();

        Validate(options, configuration);

        AddAccessTokens(services, options, configuration);
        AddCatalogue(services, options);
        AddTenantResolver(services, options);
        AddSubmissionClient(services, options);

        return services;
    }

    /// <summary>
    /// Fails at startup rather than at the first receipt. A stub that reached Production would tell users
    /// their expenses were filed when nothing left the process; a live seam with no address or credential
    /// would fail every receipt in a way that reads like an outage.
    /// </summary>
    private static void Validate(ExpenseApiOptions options, IConfiguration configuration)
    {
        var environment = configuration["ASPNETCORE_ENVIRONMENT"] ?? "Production";
        var isProduction = string.Equals(environment, "Production", StringComparison.OrdinalIgnoreCase);

        var stubbed = new List<string>();

        if (options.ResolvedCatalogueMode == ExpenseApiMode.Stub)
        {
            stubbed.Add(nameof(options.CatalogueMode));
        }

        if (options.ResolvedTenantMode == ExpenseApiMode.Stub)
        {
            stubbed.Add(nameof(options.TenantMode));
        }

        if (options.ResolvedSubmissionMode == ExpenseApiMode.Stub)
        {
            stubbed.Add(nameof(options.SubmissionMode));
        }

        // Mock is not a safer Stub: it accepts every submission and files nothing. In Production
        // it would tell people their expenses were submitted when nothing had happened.
        if (options.ResolvedSubmissionMode == ExpenseApiMode.Mock)
        {
            stubbed.Add(nameof(options.SubmissionMode) + " (Mock)");
        }

        if (isProduction && stubbed.Count > 0)
        {
            throw new InvalidOperationException(
                $"{ExpenseApiOptions.SectionName}: {string.Join(", ", stubbed)} resolve to 'Stub' but the " +
                "environment is 'Production'. Stub seams serve local mock data and never contact the " +
                "Expense API. Set them to Live with real credentials, or run outside Production.");
        }

        var live =
            options.ResolvedCatalogueMode == ExpenseApiMode.Live
            || options.ResolvedSubmissionMode == ExpenseApiMode.Live;

        if (live && string.IsNullOrWhiteSpace(options.BaseUrl))
        {
            throw new InvalidOperationException(
                $"{ExpenseApiOptions.SectionName}:BaseUrl is required when a seam is Live " +
                "(for example https://apis.justlogindevelopment.xyz).");
        }

        if (options.ResolvedIdentityMode == ExpenseApiMode.Live)
        {
            var missing = new[] { "JLHttpClient:BaseUrl", "IdentitySDK:ClientID", "IdentitySDK:ClientSecret", "IdentitySDK:Scope" }
                .Where(key => string.IsNullOrWhiteSpace(configuration[key]))
                .ToList();

            if (missing.Count > 0)
            {
                // Caught here rather than inside the SDK, whose own check throws ArgumentNullException
                // naming a property nobody configured by that name.
                throw new InvalidOperationException(
                    $"{ExpenseApiOptions.SectionName}:IdentityMode is Live but {string.Join(", ", missing)} " +
                    "are not configured. The identity server issues the company token every Expense API " +
                    "call carries; without these no call can be authenticated.");
            }
        }

        if (options.ResolvedTenantMode == ExpenseApiMode.Live
            && (options.OrganizationId is null || options.MemberId is null))
        {
            throw new InvalidOperationException(
                $"{ExpenseApiOptions.SectionName}:ConfiguredOrganizationId and ConfiguredMemberId are " +
                "required when TenantMode is Live. Member lookup by phone or email does not exist in " +
                "expense-api, so a live deployment serves one configured company until it does.");
        }
    }

    /// <summary>
    /// Chooses where the Expense API credential comes from.
    ///
    /// <c>ExpenseApi:IdentityMode=Live</c> mints a company-scoped token per company through JustLogin's
    /// identity server. Anything else uses <c>ExpenseApi:ApiKey</c> verbatim, which is what the mock
    /// endpoints check and what every existing local setup already has.
    ///
    /// The identity client is registered as a singleton by the SDK and caches a system token in memory,
    /// so this must not be resolved per request.
    /// </summary>
    private static void AddAccessTokens(
        IServiceCollection services,
        ExpenseApiOptions options,
        IConfiguration configuration)
    {
        if (options.ResolvedIdentityMode != ExpenseApiMode.Live)
        {
            services.AddSingleton<IExpenseAccessTokenProvider, ConfiguredExpenseAccessTokenProvider>();
            return;
        }

        // The SDK builds its requests against a named HttpClient it resolves itself, and reads its own
        // configuration section. Both registrations are the vendor's; calling them the way the Lambdas
        // do is what keeps this integration recognisable to whoever maintains the other three.
        services.AddJLHttpClient(configuration);
        services.AddIdentitySdk(configuration);

        // Which company, in the identity server's terms. Stub answers from the embedded fixture — the
        // mapping for our one dev company is a constant — so identity can be live while membership is
        // not. Live performs the real lookup.
        if (options.ResolvedMembershipMode == ExpenseApiMode.Live)
        {
            services
                .AddHttpClient<IJustLoginCompanyDirectory, MembershipJustLoginCompanyDirectory>((provider, client) =>
                {
                    var baseUrl = configuration["JLHttpClient:BaseUrl"];

                    if (!string.IsNullOrWhiteSpace(baseUrl))
                    {
                        client.BaseAddress = new Uri($"{baseUrl.TrimEnd('/')}/");
                    }
                })
                .AddStandardResilienceHandler();
        }
        else
        {
            services.AddSingleton<IJustLoginCompanyDirectory, MockJustLoginCompanyDirectory>();
        }

        services.AddSingleton<IExpenseAccessTokenProvider, JustLoginAccessTokenProvider>();
    }

    private static void AddCatalogue(IServiceCollection services, ExpenseApiOptions options)
    {
        if (options.ResolvedCatalogueMode == ExpenseApiMode.Stub)
        {
            services.AddSingleton<StubExpenseCatalogue>();
            services.AddSingleton<IExpenseCatalogue>(provider => new CachingExpenseCatalogue(
                provider.GetRequiredService<StubExpenseCatalogue>(),
                provider.GetRequiredService<IMemoryCache>(),
                provider.GetRequiredService<IOptions<ExpenseApiOptions>>()));

            return;
        }

        services
            .AddHttpClient<ExpenseCatalogueClient>((provider, client) =>
                Configure(client, provider.GetRequiredService<IOptions<ExpenseApiOptions>>().Value))
            .AddStandardResilienceHandler();

        services.AddSingleton<IExpenseCatalogue>(provider => new CachingExpenseCatalogue(
            provider.GetRequiredService<ExpenseCatalogueClient>(),
            provider.GetRequiredService<IMemoryCache>(),
            provider.GetRequiredService<IOptions<ExpenseApiOptions>>()));
    }

    private static void AddTenantResolver(IServiceCollection services, ExpenseApiOptions options)
    {
        if (options.ResolvedTenantMode == ExpenseApiMode.Stub)
        {
            services.AddScoped<IExpenseTenantResolver, StubExpenseTenantResolver>();
            return;
        }

        // Mock asks the membership endpoint over real HTTP with the system token — the same request a
        // real membership route would take, answered for now by Justina's own stand-in, because nothing
        // in JustLogin maps a Telegram id to a member yet (R12).
        if (options.ResolvedTenantMode == ExpenseApiMode.Mock)
        {
            services
                .AddHttpClient<IExpenseTenantResolver, MembershipExpenseTenantResolver>((provider, client) =>
                {
                    var current = provider.GetRequiredService<IOptions<ExpenseApiOptions>>().Value;

                    if (!string.IsNullOrWhiteSpace(current.ResolvedMembershipBaseUrl))
                    {
                        client.BaseAddress = new Uri($"{current.ResolvedMembershipBaseUrl.TrimEnd('/')}/");
                    }

                    client.Timeout = TimeSpan.FromSeconds(current.TimeoutSeconds);
                })
                .AddStandardResilienceHandler();

            return;
        }

        services.AddScoped<IExpenseTenantResolver, ConfiguredExpenseTenantResolver>();
    }

    private static void AddSubmissionClient(IServiceCollection services, ExpenseApiOptions options)
    {
        if (options.ResolvedSubmissionMode == ExpenseApiMode.Stub)
        {
            services.AddScoped<IExpenseApiClient, StubExpenseApiClient>();
            return;
        }

        // Mock and Live both speak the chat-scan contract; the only difference is where BaseUrl points.
        // That is the point of the mock — the payload is genuinely built, authenticated, sent and
        // parsed, and only the far end changes when it goes live.
        services
            .AddHttpClient<IExpenseApiClient, ChatScanExpenseApiClient>((provider, client) =>
                Configure(client, provider.GetRequiredService<IOptions<ExpenseApiOptions>>().Value))
            // Retries transient failures only. The submission carries an idempotency key, and a receipt
            // already created is reused rather than created again, so a retry cannot produce a second
            // expense (§33).
            .AddStandardResilienceHandler();
    }

    private static void Configure(HttpClient client, ExpenseApiOptions options)
    {
        if (!string.IsNullOrWhiteSpace(options.BaseUrl))
        {
            client.BaseAddress = new Uri($"{options.BaseUrl.TrimEnd('/')}/");
        }

        client.Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds);

        if (!string.IsNullOrWhiteSpace(options.ApiKey))
        {
            client.DefaultRequestHeaders.TryAddWithoutValidation(
                options.ApiKeyHeader,
                $"{options.ApiKeyPrefix}{options.ApiKey}");
        }
    }
}
