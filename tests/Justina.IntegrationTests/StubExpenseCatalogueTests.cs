using Justina.Expense.Application.Abstractions;
using Justina.Expense.Application.Receipts;
using Justina.Expense.Infrastructure.Api;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace Justina.IntegrationTests;

/// <summary>
/// Runs prompt construction and name resolution against the real dev tenant's catalogue, warts included.
/// </summary>
public class StubExpenseCatalogueTests
{
    private static readonly ExpenseTenant Tenant = new(
        Guid.Parse("1ba47eac-7ae7-4270-a3b8-a935f30c53ee"),
        "1BA47EAC7AE74270A3B8A935F30C53EE",
        Guid.Parse("4b07c8bf-dda4-40b7-8042-ceaea8ed3342"));

    private static async Task<ExpenseCatalogue> LoadAsync() =>
        await new StubExpenseCatalogue(NullLogger<StubExpenseCatalogue>.Instance)
            .GetAsync(Tenant, CancellationToken.None);

    [Fact]
    public async Task The_tenants_categories_and_taxes_are_loaded()
    {
        var catalogue = await LoadAsync();

        catalogue.Categories.Count.ShouldBe(15);
        catalogue.Taxes.Count.ShouldBe(7);
    }

    [Fact]
    public async Task A_category_resolves_to_the_identifier_the_expense_api_uses()
    {
        var catalogue = await LoadAsync();

        catalogue.FindCategory("Meals and Entertainment")!.Id
            .ShouldBe(Guid.Parse("279555fd-5119-4d3c-b94d-88d5d03cceb5"));

        catalogue.FindCategory("Medical Expense")!.Id
            .ShouldBe(Guid.Parse("9ef07585-4f43-43ce-a14d-1070ff941c43"));
    }

    [Fact]
    public async Task A_name_whose_double_space_the_prompt_collapses_still_resolves()
    {
        // "Employee Monthly Personal  Expense" is stored with two spaces. The prompt shows it with one,
        // so the model copies back the single-spaced form; resolution has to accept that.
        var catalogue = await LoadAsync();

        catalogue.FindCategory("Employee Monthly Personal Expense")!.Id
            .ShouldBe(Guid.Parse("9a823fdf-26ee-44e3-a186-ce015dce4437"));
    }

    [Fact]
    public async Task Tax_labels_keep_the_rate_exactly_as_the_api_rendered_it()
    {
        var catalogue = await LoadAsync();

        catalogue.Taxes.Select(tax => tax.Label).ShouldContain("GST9 (9.00%)");
        catalogue.Taxes.Select(tax => tax.Label).ShouldContain("GST No (0.00%)");

        catalogue.FindTax("GST9 (9.00%)")!.Id
            .ShouldBe(Guid.Parse("a9e8917d-54bc-4444-b014-55d221ef693c"));
    }

    [Fact]
    public async Task A_tax_whose_name_disagrees_with_its_rate_keeps_the_rate_as_the_truth()
    {
        // "GST50" is stored at 5.00%. Whatever the name suggests, the rate is what the label and the
        // expense record carry — and the label is what disambiguates it for the model.
        var catalogue = await LoadAsync();

        var tax = catalogue.FindTax("GST50 (5.00%)");

        tax.ShouldNotBeNull();
        tax.Rate.ShouldBe(5.00m);
        tax.Label.ShouldBe("GST50 (5.00%)");
    }

    [Fact]
    public async Task The_real_catalogue_fits_within_the_prompt_caps()
    {
        var catalogue = await LoadAsync();
        var prompt = ReceiptExtractionPrompt.Compose(catalogue);

        prompt.ShouldStartWith(ReceiptExtractionSchema.Instruction);
        prompt.ShouldContain("Category must be one of");
        prompt.ShouldContain("Meals and Entertainment");
        prompt.ShouldContain("GST9 (9.00%)");

        // Every category name in the fixture becomes a candidate the model may classify a receipt as, so
        // test entries are not cosmetic: a lunch bill could be filed against "test". The fixture was
        // curated for that reason, and this asserts it stays curated.
        foreach (var junk in new[] { "AAA", "asdasd", "dasdsad", "unlink payelement", "sss", "haab" })
        {
            prompt.ShouldNotContain(junk, Case.Insensitive);
        }
    }
}
