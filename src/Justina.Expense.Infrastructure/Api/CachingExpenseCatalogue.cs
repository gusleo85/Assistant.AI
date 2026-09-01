using Justina.Expense.Application.Abstractions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace Justina.Expense.Infrastructure.Api;

/// <summary>
/// Caches a company's catalogue for a short window, wrapping whichever <see cref="IExpenseCatalogue"/>
/// is registered — stub or live — so caching behaviour does not change when the source does.
///
/// The key is the organization id and nothing else. A cache shared across organizations would put one
/// company's categories into another company's prompt, which is a tenancy breach rather than a
/// performance bug.
///
/// An empty catalogue is never cached: empty means the lookup failed, and caching a failure would extend
/// a brief outage into minutes of unconstrained extraction.
/// </summary>
public sealed class CachingExpenseCatalogue(
    IExpenseCatalogue inner,
    IMemoryCache cache,
    IOptions<ExpenseApiOptions> options)
    : IExpenseCatalogue
{
    private readonly ExpenseApiOptions _options = options.Value;

    public async Task<ExpenseCatalogue> GetAsync(ExpenseTenant tenant, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tenant);

        var key = $"expense-catalogue:{tenant.OrganizationId:N}";

        if (cache.TryGetValue(key, out ExpenseCatalogue? cached) && cached is not null)
        {
            return cached;
        }

        var catalogue = await inner.GetAsync(tenant, cancellationToken).ConfigureAwait(false);

        if (!catalogue.IsEmpty)
        {
            cache.Set(key, catalogue, TimeSpan.FromMinutes(Math.Max(1, _options.CatalogueCacheMinutes)));
        }

        return catalogue;
    }
}
