using System.Globalization;
using System.Text;
using Justina.Expense.Application.Abstractions;
using Justina.Expense.Domain;

namespace Justina.Expense.Application.Receipts;

public sealed record NormalizedReceipt(ReceiptFields Fields, IReadOnlyList<ReceiptLineItem> LineItems);

/// <summary>
/// Turns untrusted Vision strings into validated domain values (§27).
/// Anything unparseable becomes <c>null</c> rather than a guess — a missing field is surfaced to the user
/// for correction, which is safer than a plausible wrong number.
/// </summary>
public static class ReceiptNormalizer
{
    private const int MaxTextLength = 256;

    private static readonly string[] DateFormats =
    [
        "yyyy-MM-dd", "yyyy/MM/dd", "dd-MM-yyyy", "dd/MM/yyyy", "MM/dd/yyyy", "d/M/yyyy", "M/d/yyyy",
        "dd.MM.yyyy", "d MMM yyyy", "dd MMM yyyy", "d MMMM yyyy", "dd MMMM yyyy",
        "MMM d, yyyy", "MMMM d, yyyy", "MMM dd yyyy", "MMMM dd yyyy", "yyyyMMdd",
    ];

    public static NormalizedReceipt Normalize(RawReceipt raw) => Normalize(raw, null);

    /// <summary>
    /// Resolves the category name and tax labels the model answered with against the company's catalogue.
    /// The model only ever supplies names; identifiers are looked up here, so an id can never be
    /// something a model made up. A name that matches nothing keeps its text and gets no id.
    /// </summary>
    public static NormalizedReceipt Normalize(RawReceipt raw, ExpenseCatalogue? catalogue)
    {
        ArgumentNullException.ThrowIfNull(raw);

        var category = Text(raw.Category);
        var categoryId = catalogue?.FindCategory(category)?.Id;

        var currency = Currency(raw.Currency);
        var currencyId = catalogue?.FindCurrency(currency)?.Id;

        var amount = PositiveAmount(raw.Amount);
        var taxAmount = NonNegativeAmount(raw.TaxAmount);

        var taxes = ResolveTaxes(raw, catalogue, amount, taxAmount);

        var fields = new ReceiptFields(
            Merchant: Text(raw.Merchant),
            Date: Date(raw.Date),
            Currency: currency,
            Amount: amount,
            Category: category,
            ReceiptNumber: Text(raw.ReceiptNumber),
            TaxAmount: taxAmount,
            CategoryId: categoryId,
            TaxIds: taxes.Select(tax => tax.Id).ToList(),
            CurrencyId: currencyId,
            TaxLabels: taxes.Select(tax => tax.Label).ToList());

        var lineItems = (raw.LineItems ?? [])
            .Select(LineItem)
            .Where(item => item is not null)
            .Select(item => item!)
            .ToList();

        return new NormalizedReceipt(fields, lineItems);
    }

    /// <summary>
    /// Decides which of the company's predefined taxes a receipt carries.
    ///
    /// <para>
    /// The amounts win over the label whenever a rate can be derived from them. That ordering was earned:
    /// a receipt reading "Inc 9% GST" for 1.68 on 20.40 was matched by the model to "GST Yes 8 (8.00%)"
    /// purely on the wording, when the arithmetic says 8.97% and the company defines a 9.00% tax. A tax
    /// filed against the wrong rate is worse than one left unresolved, because nothing about it looks
    /// wrong afterwards.
    /// </para>
    ///
    /// <para>
    /// The label is still used where arithmetic cannot help — a zero-rated tax, or a receipt that names a
    /// tax without printing its amount — and a label whose rate contradicts the arithmetic is dropped
    /// rather than trusted.
    /// </para>
    /// </summary>
    /// <returns>
    /// The matched catalogue records, not just their ids: the label travels with the id so the user can
    /// be shown which tax was chosen, in the catalogue's own words.
    /// </returns>
    private static List<ExpenseTax> ResolveTaxes(
        RawReceipt raw,
        ExpenseCatalogue? catalogue,
        decimal? amount,
        decimal? taxAmount)
    {
        if (catalogue is null)
        {
            return [];
        }

        var byLabel = (raw.Taxes ?? [])
            .Select(label => catalogue.FindTax(Text(label)))
            .Where(tax => tax is not null)
            .Select(tax => tax!)
            .DistinctBy(tax => tax.Id)
            .ToList();

        // Several taxes on one receipt is beyond what a single derived rate can express — 9% plus 7% and
        // 16% flat imply the same total. Where the labels resolved to more than one, they are the better
        // evidence and the arithmetic stands aside.
        if (byLabel.Count > 1)
        {
            return byLabel;
        }

        var derivedRate = DeriveTaxRate(amount, taxAmount);

        if (derivedRate is { } rate)
        {
            var byRate = catalogue.FindTaxByRate(rate);

            if (byRate is not null)
            {
                return [byRate];
            }

            // The arithmetic is sound but matches nothing the company defines. A label that disagrees
            // with it is not evidence of anything, so resolve nothing and let the user be asked.
            var labelled = byLabel.FirstOrDefault();

            return labelled is not null && Math.Abs(labelled.Rate - rate) <= 0.25m
                ? [labelled]
                : [];
        }

        return byLabel;
    }

    /// <summary>
    /// The rate a printed tax amount implies, as a percentage.
    ///
    /// A receipt total is normally tax-inclusive, so the base is the total less the tax. Returns null
    /// when the numbers cannot support a rate — no amounts, a tax of zero, or a tax at least as large as
    /// the total, which means the reading is wrong rather than the rate unusual.
    /// </summary>
    public static decimal? DeriveTaxRate(decimal? total, decimal? taxAmount)
    {
        if (total is not { } gross || taxAmount is not { } tax || tax <= 0m || gross <= tax)
        {
            return null;
        }

        return decimal.Round(tax / (gross - tax) * 100m, 2, MidpointRounding.ToEven);
    }

    /// <summary>
    /// Strips control characters and caps length. Content is kept as text — a receipt that prints
    /// "ignore previous instructions" is stored as a merchant name and can trigger nothing (§38).
    /// </summary>
    public static string? Text(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var builder = new StringBuilder(value.Length);
        var lastWasSpace = false;

        foreach (var character in value)
        {
            if (char.IsControl(character) || char.IsWhiteSpace(character))
            {
                if (!lastWasSpace)
                {
                    builder.Append(' ');
                    lastWasSpace = true;
                }

                continue;
            }

            builder.Append(character);
            lastWasSpace = false;
        }

        var normalized = builder.ToString().Trim();

        if (normalized.Length == 0)
        {
            return null;
        }

        return normalized.Length > MaxTextLength ? normalized[..MaxTextLength] : normalized;
    }

    public static string? Currency(string? value)
    {
        var text = Text(value);

        if (text is null)
        {
            return null;
        }

        var code = text.ToUpperInvariant();
        return Money.IsValidCurrency(code) ? code : null;
    }

    public static DateOnly? Date(string? value)
    {
        var text = Text(value);

        if (text is null)
        {
            return null;
        }

        if (DateOnly.TryParseExact(text, DateFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var exact))
        {
            return exact;
        }

        if (DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
        {
            return DateOnly.FromDateTime(parsed);
        }

        return null;
    }

    public static decimal? PositiveAmount(string? value)
    {
        var amount = Amount(value);
        return amount is > 0m ? amount : null;
    }

    public static decimal? NonNegativeAmount(string? value)
    {
        var amount = Amount(value);
        return amount is >= 0m ? amount : null;
    }

    /// <summary>
    /// Parses a printed money value. Handles both "1,234.56" and "1.234,56" by treating the rightmost
    /// separator as the decimal point when both appear.
    /// </summary>
    public static decimal? Amount(string? value)
    {
        var text = Text(value);

        if (text is null)
        {
            return null;
        }

        var digits = new StringBuilder(text.Length);

        foreach (var character in text)
        {
            if (char.IsAsciiDigit(character) || character is '.' or ',' or '-')
            {
                digits.Append(character);
            }
        }

        var candidate = digits.ToString();

        if (candidate.Length == 0)
        {
            return null;
        }

        var lastDot = candidate.LastIndexOf('.');
        var lastComma = candidate.LastIndexOf(',');

        if (lastDot >= 0 && lastComma >= 0)
        {
            candidate = lastDot > lastComma
                ? candidate.Replace(",", string.Empty, StringComparison.Ordinal)
                : candidate.Replace(".", string.Empty, StringComparison.Ordinal).Replace(',', '.');
        }
        else if (lastComma >= 0)
        {
            // A single comma with exactly three trailing digits is a thousands separator, not a decimal point.
            var trailing = candidate.Length - lastComma - 1;
            candidate = trailing == 3
                ? candidate.Replace(",", string.Empty, StringComparison.Ordinal)
                : candidate.Replace(',', '.');
        }

        return decimal.TryParse(candidate, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed)
            ? decimal.Round(parsed, 2, MidpointRounding.ToEven)
            : null;
    }

    private static ReceiptLineItem? LineItem(RawReceiptLineItem raw)
    {
        var description = Text(raw.Description);

        if (description is null)
        {
            return null;
        }

        return new ReceiptLineItem(
            description,
            Amount(raw.Quantity) ?? 1m,
            Amount(raw.UnitPrice) ?? 0m,
            Amount(raw.Amount) ?? 0m);
    }
}
