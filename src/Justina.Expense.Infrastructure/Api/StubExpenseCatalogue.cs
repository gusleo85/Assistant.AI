using Justina.Expense.Application.Abstractions;
using Justina.Expense.Application.Receipts;
using Microsoft.Extensions.Logging;

namespace Justina.Expense.Infrastructure.Api;

/// <summary>
/// A fixed catalogue shaped like the one the Expense API returns, used while
/// <see cref="ExpenseApiOptions.Mode"/> is <c>Stub</c>.
///
/// It exists so the whole conversation — photo in, extracted, category and taxes constrained to a real
/// list, shown, edited, confirmed — can be exercised before any JustLogin credential exists. The
/// category names are the ones the Lambda's prompt names explicitly, so behaviour observed here is
/// representative of the live lists rather than of invented data.
///
/// The identifiers are fixed rather than random: a stub that returned new GUIDs on every call would make
/// a stored receipt's <c>CategoryId</c> stop resolving after a restart.
/// </summary>
public sealed class StubExpenseCatalogue(ILogger<StubExpenseCatalogue> logger) : IExpenseCatalogue
{
    private static readonly ExpenseCatalogue Catalogue = Build();

    public Task<ExpenseCatalogue> GetAsync(ExpenseTenant tenant, CancellationToken cancellationToken)
    {
        logger.LogWarning(
            "Expense catalogue is running in STUB mode: {CategoryCount} categories and {TaxCount} taxes " +
            "come from fixed local data, not from the Expense API",
            Catalogue.Categories.Count,
            Catalogue.Taxes.Count);

        return Task.FromResult(Catalogue);
    }

    private static ExpenseCatalogue Build()
    {
        ExpenseCategory Category(string id, string name, string accountCode) =>
            new(Guid.Parse(id), name, accountCode);

        ExpenseTax Tax(string id, string name, decimal rate) =>
            new(Guid.Parse(id), name, rate, ReceiptExtractionPrompt.TaxLabel(name, rate));

        return new ExpenseCatalogue(
            [
                Category("11111111-0000-4000-8000-000000000001", "Meals and Entertainment", "6100"),
                Category("11111111-0000-4000-8000-000000000002", "Medical Expense", "6110"),
                Category("11111111-0000-4000-8000-000000000003", "Medicine Purchase", "6111"),
                Category("11111111-0000-4000-8000-000000000004", "Accommodation Expense", "6200"),
                Category("11111111-0000-4000-8000-000000000005", "Transportation", "6210"),
                Category("11111111-0000-4000-8000-000000000006", "Airfare", "6211"),
                Category("11111111-0000-4000-8000-000000000007", "Office Supplies", "6300"),
                Category("11111111-0000-4000-8000-000000000008", "Telecommunication", "6310"),
                Category("11111111-0000-4000-8000-000000000009", "Client Entertainment", "6400"),
                Category("11111111-0000-4000-8000-00000000000a", "Training and Seminar", "6500"),
                Category("11111111-0000-4000-8000-00000000000b", "Uncategorized", "6900"),
            ],
            [
                // 9.00% rather than 9%: the live API renders the rate at the decimal scale it stores, and
                // the label is matched as a string, so the stub must carry the same scale.
                Tax("22222222-0000-4000-8000-000000000001", "GST", 9.00m),
                Tax("22222222-0000-4000-8000-000000000002", "GST", 8.00m),
                Tax("22222222-0000-4000-8000-000000000003", "GST", 7.00m),
            ]);
    }
}
