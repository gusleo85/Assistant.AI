using Justina.Core.Domain.Results;
using Justina.Expense.Application.Abstractions;
using Microsoft.Extensions.Options;

namespace Justina.Expense.Infrastructure.Api;

/// <summary>
/// The credential straight out of configuration — <c>ExpenseApi:ApiKey</c>, unchanged per company.
///
/// This is what the mock endpoints authenticate against, and what a live seam falls back to when no
/// identity server is configured. It is deliberately not a silent fallback for a misconfigured identity
/// server: it is selected explicitly, and it refuses when the key is absent rather than sending an
/// empty Authorization header and letting the API return an opaque 401.
/// </summary>
public sealed class ConfiguredExpenseAccessTokenProvider(IOptions<ExpenseApiOptions> options)
    : IExpenseAccessTokenProvider
{
    private readonly ExpenseApiOptions _options = options.Value;

    public Task<Result<string>> GetAsync(ExpenseTenant tenant, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_options.ApiKey))
        {
            return Task.FromResult(Result.Failure<string>(
                ErrorCodes.NotAvailable,
                "The expense system is not configured with a credential."));
        }

        return Task.FromResult(Result.Success(_options.ApiKey));
    }
}
