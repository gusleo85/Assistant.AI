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

/// <summary>
/// A matched tax carries its catalogue label, so the confirmation can name the tax rather than show a
/// bare amount. The naming is the check: "GST: SGD 1.68" reads the same whether 9% or 8% was matched,
/// and the wrong one is invisible once filed.
/// </summary>
public class TaxLabelTests
{
    private static readonly Guid GstNineId = Guid.Parse("33333333-0000-4000-8000-000000000009");
    private static readonly Guid GstEightId = Guid.Parse("33333333-0000-4000-8000-000000000008");

    private static readonly ExpenseCatalogue Catalogue = new(
        [],
        [
            new ExpenseTax(GstNineId, "GST9", 9.00m, "GST9 (9.00%)"),
            new ExpenseTax(GstEightId, "GST Yes 8", 8.00m, "GST Yes 8 (8.00%)"),
        ],
        []);

    private static RawReceipt Raw(string amount, string taxAmount, params string[] taxes) =>
        new(
            Merchant: "Ya Kun Kaya Toast",
            Date: "2023-06-19",
            Currency: "SGD",
            Amount: amount,
            Category: null,
            ReceiptNumber: "8309",
            TaxAmount: taxAmount,
            LineItems: null,
            Taxes: taxes);

    [Fact]
    public void A_matched_tax_carries_the_catalogue_label_not_the_receipt_wording()
    {
        // The receipt says "Inc 9% GST"; the company calls it "GST9". What gets shown is the company's
        // name, because that is the tax the expense will actually be filed against.
        var normalized = ReceiptNormalizer.Normalize(Raw("20.40", "1.68", "Inc 9% GST"), Catalogue);

        normalized.Fields.TaxIds.ShouldBe([GstNineId]);
        normalized.Fields.TaxLabels.ShouldBe(["GST9 (9.00%)"]);
    }

    [Fact]
    public void An_unmatched_tax_carries_no_label()
    {
        // 20.40 with 3.00 of tax derives 17.24%, which matches neither predefined rate.
        var normalized = ReceiptNormalizer.Normalize(Raw("20.40", "3.00", "Service Tax"), Catalogue);

        normalized.Fields.TaxIds.ShouldBeEmpty();
        normalized.Fields.TaxLabels.ShouldBeEmpty();
    }

    [Fact]
    public void The_label_survives_onto_the_receipt()
    {
        var receipt = Receipt.Create(Guid.NewGuid(), "media-1", null, DateTimeOffset.UtcNow, 1);
        receipt.BeginExtraction(DateTimeOffset.UtcNow);

        var normalized = ReceiptNormalizer.Normalize(Raw("20.40", "1.68", "Inc 9% GST"), Catalogue);
        receipt.CompleteExtraction(normalized.Fields, normalized.LineItems, DateTimeOffset.UtcNow);

        receipt.TaxLabels.ShouldBe(["GST9 (9.00%)"]);
        ReceiptSnapshot.From(receipt).Taxes.ShouldBe(["GST9 (9.00%)"]);
    }

    [Fact]
    public void A_duplicate_identifier_takes_its_label_with_it()
    {
        // Ids and labels must stay aligned through de-duplication. Were they de-duplicated separately,
        // the second tax here would end up wearing the first one's name.
        var fields = new ReceiptFields(
            Merchant: "Ya Kun Kaya Toast",
            Date: new DateOnly(2023, 6, 19),
            Currency: "SGD",
            Amount: 20.40m,
            Category: null,
            ReceiptNumber: null,
            TaxAmount: 1.68m,
            TaxIds: [GstNineId, GstNineId, GstEightId],
            TaxLabels: ["GST9 (9.00%)", "GST9 (9.00%)", "GST Yes 8 (8.00%)"]);

        var receipt = Receipt.Create(Guid.NewGuid(), "media-1", null, DateTimeOffset.UtcNow, 1);
        receipt.BeginExtraction(DateTimeOffset.UtcNow);
        receipt.CompleteExtraction(fields, [], DateTimeOffset.UtcNow);

        receipt.TaxIds.ShouldBe([GstNineId, GstEightId]);
        receipt.TaxLabels.ShouldBe(["GST9 (9.00%)", "GST Yes 8 (8.00%)"]);
    }

    [Fact]
    public void Labels_that_do_not_line_up_with_the_identifiers_are_dropped_entirely()
    {
        // Half a set of names is worse than none: the user cannot tell an unnamed tax from one whose
        // name failed to resolve, so nothing is shown rather than something possibly wrong.
        var fields = new ReceiptFields(
            Merchant: "Ya Kun Kaya Toast",
            Date: new DateOnly(2023, 6, 19),
            Currency: "SGD",
            Amount: 20.40m,
            Category: null,
            ReceiptNumber: null,
            TaxAmount: 1.68m,
            TaxIds: [GstNineId, GstEightId],
            TaxLabels: ["GST9 (9.00%)"]);

        var receipt = Receipt.Create(Guid.NewGuid(), "media-1", null, DateTimeOffset.UtcNow, 1);
        receipt.BeginExtraction(DateTimeOffset.UtcNow);
        receipt.CompleteExtraction(fields, [], DateTimeOffset.UtcNow);

        receipt.TaxIds.Count.ShouldBe(2);
        receipt.TaxLabels.ShouldBeEmpty();
    }
}
