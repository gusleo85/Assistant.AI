using System.Globalization;
using System.Text;
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

    public static NormalizedReceipt Normalize(RawReceipt raw)
    {
        ArgumentNullException.ThrowIfNull(raw);

        var fields = new ReceiptFields(
            Merchant: Text(raw.Merchant),
            Date: Date(raw.Date),
            Currency: Currency(raw.Currency),
            Amount: PositiveAmount(raw.Amount),
            Category: Text(raw.Category),
            ReceiptNumber: Text(raw.ReceiptNumber),
            TaxAmount: NonNegativeAmount(raw.TaxAmount));

        var lineItems = (raw.LineItems ?? [])
            .Select(LineItem)
            .Where(item => item is not null)
            .Select(item => item!)
            .ToList();

        return new NormalizedReceipt(fields, lineItems);
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
