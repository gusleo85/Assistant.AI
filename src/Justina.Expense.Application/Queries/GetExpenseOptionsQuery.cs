using Justina.Core.Application.Messaging;
using Justina.Core.Domain.Identity;
using Justina.Core.Domain.Results;
using Justina.Expense.Application.Abstractions;

namespace Justina.Expense.Application.Queries;

/// <summary>
/// The values the company actually accepts, so a question to the user can offer real choices rather than
/// an open prompt. "Which category?" is a poor question; "Meals and Entertainment, Travel or Office
/// Supplies?" is answerable.
/// </summary>
public sealed record ExpenseOptions(
    IReadOnlyList<string> Categories,
    IReadOnlyList<string> Taxes,
    IReadOnlyList<string> Currencies)
{
    public bool IsEmpty => Categories.Count == 0 && Taxes.Count == 0 && Currencies.Count == 0;
}

public sealed record GetExpenseOptionsQuery(RequestContext Context)
    : IQuery<ExpenseOptions>, IRequireCapability
{
    public string RequiredCapability => Capabilities.ExpenseRead;
}

public sealed class GetExpenseOptionsQueryHandler(
    IExpenseTenantResolver tenants,
    IExpenseCatalogue catalogue)
    : IQueryHandler<GetExpenseOptionsQuery, ExpenseOptions>
{
    /// <summary>
    /// Currencies are capped. A company can carry the full ISO list, and a message naming two hundred
    /// codes helps nobody — the agent asks for a code, and the tool validates whatever comes back.
    /// </summary>
    private const int MaxCurrencies = 40;

    public async Task<Result<ExpenseOptions>> HandleAsync(
        GetExpenseOptionsQuery query,
        CancellationToken cancellationToken)
    {
        var tenant = await tenants.ResolveAsync(query.Context, cancellationToken).ConfigureAwait(false);

        if (tenant.IsFailure)
        {
            return Result.Failure<ExpenseOptions>(tenant.Error);
        }

        var loaded = await catalogue.GetAsync(tenant.Value, cancellationToken).ConfigureAwait(false);

        return Result.Success(new ExpenseOptions(
            loaded.Categories.Select(c => c.Name).Order(StringComparer.OrdinalIgnoreCase).ToList(),
            loaded.Taxes.Select(t => t.Label).Order(StringComparer.OrdinalIgnoreCase).ToList(),
            loaded.Currencies
                .Select(c => c.Code)
                .Order(StringComparer.OrdinalIgnoreCase)
                .Take(MaxCurrencies)
                .ToList()));
    }
}
