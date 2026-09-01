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
        var section = configuration.GetSection(ExpenseApiOptions.SectionName);
        services.Configure<ExpenseApiOptions>(section);

        services.AddSingleton<IModelConfiguration, ExpenseModelConfiguration>();
        services.AddScoped<IReceiptRepository, ReceiptRepository>();

        var mode = section.GetValue(ExpenseApiOptions.ModeKey, ExpenseApiMode.Stub);

        if (mode == ExpenseApiMode.Stub)
        {
            // A stub that reached Production would tell users their expenses were filed when nothing left
            // the process. Refusing to start is the only failure mode here that cannot be missed.
            var environment = configuration["ASPNETCORE_ENVIRONMENT"] ?? "Production";

            if (string.Equals(environment, "Production", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"{ExpenseApiOptions.SectionName}:Mode is 'Stub' but the environment is 'Production'. " +
                    "Stub mode records submissions locally and never contacts the Expense API. Set " +
                    $"{ExpenseApiOptions.SectionName}__Mode=Live with real credentials, or run outside Production.");
            }

            services.AddSingleton<IExpenseCatalogue, StubExpenseCatalogue>();
            services.AddScoped<IExpenseTenantResolver, StubExpenseTenantResolver>();
            services.AddScoped<IExpenseApiClient, StubExpenseApiClient>();

            return services;
        }

        // Live mode: the submission client exists and targets a provisional contract (plan risk R1), so it
        // is wired up and stays compiled and testable. The catalogue and tenant resolvers against
        // JustLogin are not written yet, so startup still refuses — naming exactly what is missing beats
        // starting up and filing every expense with no category against an unknown company.
        services.AddLiveExpenseApiClient();

        throw new InvalidOperationException(
            $"{ExpenseApiOptions.SectionName}:Mode is 'Live', but the live expense catalogue and tenant " +
            "resolver are not implemented yet: they need the JustLogin identity credentials and the " +
            "member-lookup contract. Use Mode=Stub until those land.");
    }

    private static IServiceCollection AddLiveExpenseApiClient(
        this IServiceCollection services)
    {
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
