using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using Justina.Expense.Application.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Justina.Expense.Infrastructure.Api;

/// <summary>
/// Reads the company's category and tax lists from the Expense API.
///
/// It calls the <c>/list/{organizationId}</c> routes rather than the bare <c>/Categories</c> and
/// <c>/Taxes</c> ones: those take the organization in the path instead of reading the token's
/// <c>CompanyGUID</c> claim, so a system token is sufficient and no company-scoped token has to be
/// minted per conversation.
///
/// Every failure returns <see cref="ExpenseCatalogue.Empty"/> rather than an error. Extraction then runs
/// unconstrained, which is the behaviour that shipped before there was a catalogue at all. A receipt a
/// user already sent must not be lost because a list endpoint was slow.
/// </summary>
public sealed class ExpenseCatalogueClient(
    HttpClient httpClient,
    IOptions<ExpenseApiOptions> options,
    ILogger<ExpenseCatalogueClient> logger)
    : IExpenseCatalogue
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly ExpenseApiOptions _options = options.Value;

    public async Task<ExpenseCatalogue> GetAsync(ExpenseTenant tenant, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tenant);

        var categories = await ReadAsync(_options.CategoriesPath, tenant, cancellationToken).ConfigureAwait(false);
        var taxes = await ReadAsync(_options.TaxesPath, tenant, cancellationToken).ConfigureAwait(false);
        var currencies = await ReadAsync(_options.CurrenciesPath, tenant, cancellationToken).ConfigureAwait(false);

        if (categories.Count == 0 && taxes.Count == 0 && currencies.Count == 0)
        {
            return ExpenseCatalogue.Empty;
        }

        return new ExpenseCatalogue(
            categories.Select(item => new ExpenseCategory(item.Id, item.Name)).ToList(),
            taxes.Select(ToTax).ToList(),
            // On a currency row the API puts the ISO code in "name" and the full name in "attribute".
            currencies
                .Select(item => new ExpenseCurrency(item.Id, item.Name, item.Attribute ?? item.Name, item.ExchangeRate))
                .ToList());
    }

    /// <summary>
    /// Both list endpoints answer with the same <c>ListItemResponse</c> shape; for a tax, <c>attribute</c>
    /// carries the rate. The label is built from that string exactly as received — parsing to decimal and
    /// reformatting would turn "9.00" into "9", and the label is the key the model's answer is matched
    /// against.
    /// </summary>
    private static ExpenseTax ToTax(ExpenseListItem item)
    {
        var rate = decimal.TryParse(
            item.Attribute,
            NumberStyles.Number,
            CultureInfo.InvariantCulture,
            out var parsed)
            ? parsed
            : 0m;

        return new ExpenseTax(item.Id, item.Name, rate, string.Concat(item.Name, " (", item.Attribute, "%)"));
    }

    private async Task<IReadOnlyList<ExpenseListItem>> ReadAsync(
        string pathTemplate,
        ExpenseTenant tenant,
        CancellationToken cancellationToken)
    {
        var path = string.Format(CultureInfo.InvariantCulture, pathTemplate, tenant.OrganizationId);

        try
        {
            using var response = await httpClient.GetAsync(path, cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning(
                    "The Expense API returned {StatusCode} for {Path}; continuing without that list",
                    (int)response.StatusCode,
                    pathTemplate);

                return [];
            }

            var items = await response.Content
                .ReadFromJsonAsync<List<ExpenseListItem>>(JsonOptions, cancellationToken)
                .ConfigureAwait(false);

            return items ?? [];
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException
            || (exception is TaskCanceledException && !cancellationToken.IsCancellationRequested))
        {
            logger.LogWarning(exception, "Could not read {Path} from the Expense API", pathTemplate);
            return [];
        }
    }
}
