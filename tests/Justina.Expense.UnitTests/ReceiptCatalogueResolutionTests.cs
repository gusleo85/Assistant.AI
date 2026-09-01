using Justina.Expense.Application.Abstractions;
using Justina.Expense.Application.Receipts;
using Justina.Expense.Domain;
using Shouldly;

namespace Justina.Expense.UnitTests;

/// <summary>
/// The model answers with names; identifiers are resolved here. These tests pin the rule that an id is
/// never something a model produced, and that an unresolvable name still keeps its text.
/// </summary>
public class ReceiptCatalogueResolutionTests
{
    private static readonly Guid MealsId = Guid.Parse("11111111-0000-4000-8000-000000000001");
    private static readonly Guid GstNineId = Guid.Parse("22222222-0000-4000-8000-000000000001");
    private static readonly Guid GstSevenId = Guid.Parse("22222222-0000-4000-8000-000000000003");

    private static readonly ExpenseCatalogue Catalogue = new(
        [
            new ExpenseCategory(MealsId, "Meals and Entertainment", "6100"),
            new ExpenseCategory(Guid.NewGuid(), "Airfare", "6211"),
        ],
        [
            new ExpenseTax(GstNineId, "GST", 9.00m, "GST (9.00%)"),
            new ExpenseTax(GstSevenId, "GST", 7.00m, "GST (7.00%)"),
        ],
        [
            new ExpenseCurrency(SgdId, "SGD", "Singapore Dollar", 1m),
        ]);

    private static readonly Guid SgdId = Guid.Parse("2ed20622-fc20-4d4d-8832-9b6e687cc087");

    private static RawReceipt Raw(string? category, params string[] taxes) =>
        new(
            Merchant: "Lulu Min Restaurant",
            Date: "2026-02-12",
            Currency: "SGD",
            Amount: "80.00",
            Category: category,
            ReceiptNumber: "184178",
            TaxAmount: "6.60",
            LineItems: null,
            Taxes: taxes);

    [Fact]
    public void A_category_name_resolves_to_its_identifier()
    {
        var normalized = ReceiptNormalizer.Normalize(Raw("Meals and Entertainment"), Catalogue);

        normalized.Fields.Category.ShouldBe("Meals and Entertainment");
        normalized.Fields.CategoryId.ShouldBe(MealsId);
    }

    [Theory]
    [InlineData("meals and entertainment")]
    [InlineData("MEALS AND ENTERTAINMENT")]
    [InlineData("  Meals and Entertainment  ")]
    public void Case_and_surrounding_space_do_not_lose_the_identifier(string answer)
    {
        // The Lambda matches with an ordinal ==, so any of these silently drops the id there. Justina
        // matches case-insensitively on purpose; the divergence is deliberate, not accidental.
        ReceiptNormalizer.Normalize(Raw(answer), Catalogue).Fields.CategoryId.ShouldBe(MealsId);
    }

    [Fact]
    public void A_category_that_is_not_in_the_catalogue_keeps_its_text_and_gets_no_identifier()
    {
        var normalized = ReceiptNormalizer.Normalize(Raw("Sorcery"), Catalogue);

        normalized.Fields.Category.ShouldBe("Sorcery");
        normalized.Fields.CategoryId.ShouldBeNull();
    }

    [Fact]
    public void Tax_labels_resolve_to_identifiers_and_unmatched_ones_are_dropped()
    {
        var normalized = ReceiptNormalizer.Normalize(
            Raw("Airfare", "GST (9.00%)", "VAT (20.00%)"),
            Catalogue);

        normalized.Fields.TaxIds.ShouldBe([GstNineId]);
    }

    [Fact]
    public void The_same_tax_returned_twice_is_recorded_once()
    {
        var normalized = ReceiptNormalizer.Normalize(
            Raw("Airfare", "GST (9.00%)", "GST (9.00%)"),
            Catalogue);

        normalized.Fields.TaxIds.ShouldNotBeNull().Count.ShouldBe(1);
    }

    [Fact]
    public void Two_rates_of_the_same_tax_stay_distinct()
    {
        // "GST" alone is ambiguous across 7%, 8% and 9%. The rate is what makes the label a key.
        var normalized = ReceiptNormalizer.Normalize(
            Raw("Airfare", "GST (9.00%)", "GST (7.00%)"),
            Catalogue);

        normalized.Fields.TaxIds.ShouldBe([GstNineId, GstSevenId], ignoreOrder: true);
    }

    [Fact]
    public void Without_a_catalogue_nothing_is_resolved_and_the_text_survives()
    {
        var normalized = ReceiptNormalizer.Normalize(Raw("Meals and Entertainment", "GST (9.00%)"));

        normalized.Fields.Category.ShouldBe("Meals and Entertainment");
        normalized.Fields.CategoryId.ShouldBeNull();
        normalized.Fields.TaxIds.ShouldBeEmpty();
    }

    [Fact]
    public void Editing_the_category_name_by_hand_drops_the_old_identifier()
    {
        // A receipt whose name and id point at two different categories is worse than one with no id,
        // because the id is what the expense system actually files against.
        var receipt = Receipt.Create(Guid.NewGuid(), "media-1", null, DateTimeOffset.UtcNow, 1);
        receipt.BeginExtraction(DateTimeOffset.UtcNow);

        var normalized = ReceiptNormalizer.Normalize(Raw("Meals and Entertainment"), Catalogue);
        receipt.CompleteExtraction(normalized.Fields, normalized.LineItems, DateTimeOffset.UtcNow);
        receipt.CategoryId.ShouldBe(MealsId);

        receipt.ApplyChanges(
            [new ReceiptFieldChange { Field = ReceiptField.Category, StringValue = "Airfare" }],
            "user",
            DateTimeOffset.UtcNow);

        receipt.Category.ShouldBe("Airfare");
        receipt.CategoryId.ShouldBeNull();
    }
}
