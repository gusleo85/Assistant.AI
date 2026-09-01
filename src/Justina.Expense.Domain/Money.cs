using Justina.Core.Domain;

namespace Justina.Expense.Domain;

/// <summary>
/// A validated amount plus ISO-4217 currency.
/// Used at the boundaries (validation and API submission); the entity persists the primitives so the
/// SQL Server column types stay explicit and queryable.
/// </summary>
public readonly record struct Money
{
    private Money(decimal amount, string currency)
    {
        Amount = amount;
        Currency = currency;
    }

    public decimal Amount { get; }

    public string Currency { get; }

    public static bool IsValidCurrency(string? currency) =>
        currency is { Length: 3 } && currency.All(char.IsAsciiLetterUpper);

    public static Money Create(decimal amount, string currency)
    {
        if (amount <= 0m)
        {
            throw new DomainException("A monetary amount must be greater than zero.");
        }

        if (!IsValidCurrency(currency))
        {
            throw new DomainException($"'{currency}' is not a valid ISO-4217 currency code.");
        }

        // Receipt values are two-decimal money; anything finer is a rounding artefact from extraction.
        return new Money(decimal.Round(amount, 2, MidpointRounding.ToEven), currency);
    }

    public override string ToString() => $"{Currency} {Amount:0.00}";
}
