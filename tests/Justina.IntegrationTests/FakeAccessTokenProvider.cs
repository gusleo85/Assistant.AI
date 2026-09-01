using Justina.Core.Domain.Results;
using Justina.Expense.Application.Abstractions;

namespace Justina.IntegrationTests;

/// <summary>
/// Stands in for the identity server. The tests here are about the API clients — payload shape, failure
/// translation, timeouts — and none of them is about how a credential was obtained.
/// </summary>
internal sealed class FakeAccessTokenProvider(string? token = "test-token") : IExpenseAccessTokenProvider
{
    public int Calls { get; private set; }

    public ExpenseTenant? LastTenant { get; private set; }

    public Task<Result<string>> GetAsync(ExpenseTenant tenant, CancellationToken cancellationToken)
    {
        Calls++;
        LastTenant = tenant;

        return Task.FromResult(token is null
            ? Result.Failure<string>(ErrorCodes.ExternalApiFailed, "Identity is unavailable.")
            : Result.Success(token));
    }
}
