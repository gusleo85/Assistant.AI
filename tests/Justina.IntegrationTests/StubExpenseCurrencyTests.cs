using Justina.Expense.Application.Abstractions;
using Justina.Expense.Application.Receipts;
using Justina.Expense.Infrastructure.Api;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace Justina.IntegrationTests;

/// <summary>
/// Currency resolution against the tenant's real currency list.
///
/// This exists because of an observed failure: a live Telegram receipt from an Indonesian A&amp;W was
/// extracted with <c>currency: null</c> and amount 102000, which left it unsubmittable. The base
/// instruction only asked for an ISO code "if shown or unambiguous", and that receipt showed none.
/// </summary>
public class StubExpenseCurrencyTests
{
    private static readonly ExpenseTenant Tenant = new(
        Guid.Parse("1ba47eac-7ae7-4270-a3b8-a935f30c53ee"),
        "1BA47EAC7AE74270A3B8A935F30C53EE",
        Guid.Parse("4b07c8bf-dda4-40b7-8042-ceaea8ed3342"));

    private static async Task<ExpenseCatalogue> LoadAsync() =>
        await new StubExpenseCatalogue(NullLogger<StubExpenseCatalogue>.Instance)
            .GetAsync(Tenant, CancellationToken.None);

    [Fact]
    public async Task The_tenants_currencies_are_loaded()
    {
        var catalogue = await LoadAsync();

        catalogue.Currencies.Count.ShouldBe(40);
    }

    [Fact]
    public async Task An_iso_code_resolves_to_the_identifier_an_expense_record_references()
    {
        var catalogue = await LoadAsync();

        catalogue.FindCurrency("SGD")!.Id.ShouldBe(Guid.Parse("2ed20622-fc20-4d4d-8832-9b6e687cc087"));
        catalogue.FindCurrency("IDR")!.Id.ShouldBe(Guid.Parse("0688d620-011d-478e-ac42-16ff5a42a9ef"));
    }

    [Fact]
    public async Task The_base_currency_is_the_one_the_organization_names()
    {
        // organization.json carries baseCurrencyId 2ed20622… — SGD, at an exchange rate of exactly 1.
        var catalogue = await LoadAsync();

        var sgd = catalogue.FindCurrency("sgd");

        sgd.ShouldNotBeNull();
        sgd.ExchangeRate.ShouldBe(1m);
        sgd.Name.ShouldBe("Singapore Dollar");
    }

    [Fact]
    public async Task A_currency_the_company_does_not_claim_in_resolves_to_nothing()
    {
        var catalogue = await LoadAsync();

        catalogue.FindCurrency("XPF").ShouldBeNull();
    }

    [Fact]
    public async Task An_extracted_currency_carries_its_identifier_into_the_domain()
    {
        var catalogue = await LoadAsync();

        var raw = new RawReceipt(
            Merchant: "A&W Restoran Khas Amerika",
            Date: "2026-08-31",
            Currency: "IDR",
            Amount: "102000",
            Category: "Meals and Entertainment",
            ReceiptNumber: "DT1BMN02082026/06902",
            TaxAmount: null,
            LineItems: null);

        var normalized = ReceiptNormalizer.Normalize(raw, catalogue);

        normalized.Fields.Currency.ShouldBe("IDR");
        normalized.Fields.CurrencyId.ShouldBe(Guid.Parse("0688d620-011d-478e-ac42-16ff5a42a9ef"));
    }

    [Fact]
    public async Task The_prompt_lists_the_allowed_codes_and_forbids_defaulting_to_usd()
    {
        var catalogue = await LoadAsync();
        var prompt = ReceiptExtractionPrompt.Compose(catalogue);

        prompt.ShouldContain("IDR");
        prompt.ShouldContain("SGD");
        prompt.ShouldContain("Rp is IDR");
        prompt.ShouldContain("never default to USD");
    }
}
