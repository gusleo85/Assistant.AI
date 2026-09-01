using Justina.Core.Infrastructure.Persistence;
using Justina.Expense.Application.Abstractions;
using Justina.Expense.Infrastructure.Api;
using Justina.Expense.Infrastructure.Persistence;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Justina.Expense.Infrastructure;

public static class ExpenseInfrastructureServiceCollectionExtensions
{
    public static IServiceCollection AddExpenseInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.Configure<ExpenseApiOptions>(configuration.GetSection(ExpenseApiOptions.SectionName));

        services.AddSingleton<IModelConfiguration, ExpenseModelConfiguration>();
        services.AddScoped<IReceiptRepository, ReceiptRepository>();

        services
            .AddHttpClient<IExpenseApiClient, ExpenseApiClient>((provider, client) =>
            {
                var options = provider.GetRequiredService<IOptions<ExpenseApiOptions>>().Value;

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
            })
            // Retries transient failures only. The submission carries an idempotency key, so a retry that
            // the API already processed resolves to the same expense rather than a second one (§33).
            .AddStandardResilienceHandler();

        return services;
    }
}
