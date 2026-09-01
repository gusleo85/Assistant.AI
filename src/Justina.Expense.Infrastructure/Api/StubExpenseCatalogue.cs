using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Justina.Expense.Application.Abstractions;
using Justina.Expense.Application.Receipts;
using Microsoft.Extensions.Logging;

namespace Justina.Expense.Infrastructure.Api;

/// <summary>
/// One row of <c>GET /expense/v1/Categories/list/{organizationId}</c> or
/// <c>/Taxes/list/{organizationId}</c>. Both endpoints return the same <c>ListItemResponse</c> shape, and
/// <c>attribute</c> carries different data in each: for a category it is <c>IsAttachmentMandatory</c>
/// ("True"/"False"), for a tax it is the rate ("9.00"). That overloading is the API's, not ours.
/// </summary>
public sealed record ExpenseListItem
{
    [JsonPropertyName("id")]
    public Guid Id { get; init; }

    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("attribute")]
    public string? Attribute { get; init; }

    [JsonPropertyName("description")]
    public string? Description { get; init; }

    [JsonPropertyName("isDefault")]
    public bool IsDefault { get; init; }

    /// <summary>Present only on currencies; the rate against the organization's base currency.</summary>
    [JsonPropertyName("exchangeRate")]
    public decimal ExchangeRate { get; init; }
}

/// <summary>
/// The company's real category and tax lists, captured from the dev tenant and embedded in this
/// assembly, served while <see cref="ExpenseApiOptions.Mode"/> is <c>Stub</c>.
///
/// Real data rather than tidy invented data on purpose: this tenant's list contains test entries
/// ("AAA", "asd", "sss"), a category whose name is eighteen letters of "a", and tax names that disagree
/// with their own rates. Prompt construction and name resolution have to survive that, and they only
/// get to prove it against the thing they will actually meet.
/// </summary>
public sealed class StubExpenseCatalogue(ILogger<StubExpenseCatalogue> logger) : IExpenseCatalogue
{
    private const string CategoriesResource = "Justina.Expense.Infrastructure.MockData.categories.json";
    private const string TaxesResource = "Justina.Expense.Infrastructure.MockData.taxes.json";
    private const string CurrenciesResource = "Justina.Expense.Infrastructure.MockData.currencies.json";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private static readonly Lazy<ExpenseCatalogue> Catalogue = new(Build);

    public Task<ExpenseCatalogue> GetAsync(ExpenseTenant tenant, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tenant);

        var catalogue = Catalogue.Value;

        logger.LogWarning(
            "STUB catalogue: {CategoryCount} categories, {TaxCount} taxes and {CurrencyCount} currencies " +
            "for organization {OrganizationId} come from embedded mock data, not from the Expense API",
            catalogue.Categories.Count,
            catalogue.Taxes.Count,
            catalogue.Currencies.Count,
            tenant.OrganizationId);

        return Task.FromResult(catalogue);
    }

    private static ExpenseCatalogue Build()
    {
        var categories = Read(CategoriesResource)
            .Select(item => new ExpenseCategory(item.Id, item.Name))
            .ToList();

        var taxes = Read(TaxesResource)
            .Select(item =>
            {
                // The rate arrives as the string the API rendered, and the label must reproduce that
                // rendering exactly — it is the key the model's answer is matched against. Parsing to
                // decimal and reformatting would turn "9.00" into "9" and break every match.
                var rate = decimal.TryParse(
                    item.Attribute,
                    NumberStyles.Number,
                    CultureInfo.InvariantCulture,
                    out var parsed)
                    ? parsed
                    : 0m;

                var label = string.Concat(item.Name, " (", item.Attribute, "%)");

                return new ExpenseTax(item.Id, item.Name, rate, label);
            })
            .ToList();

        // On a currency row the API puts the ISO code in "name" and the full name in "attribute".
        var currencies = Read(CurrenciesResource)
            .Select(item => new ExpenseCurrency(
                item.Id,
                item.Name,
                item.Attribute ?? item.Name,
                item.ExchangeRate))
            .ToList();

        return new ExpenseCatalogue(categories, taxes, currencies);
    }

    private static IReadOnlyList<ExpenseListItem> Read(string resourceName)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException(
                $"Embedded mock data '{resourceName}' is missing from {nameof(Justina.Expense.Infrastructure)}.");

        return JsonSerializer.Deserialize<List<ExpenseListItem>>(stream, JsonOptions) ?? [];
    }
}
