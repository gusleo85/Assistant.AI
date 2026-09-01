using System.Globalization;
using System.Text;
using Justina.Expense.Application.Abstractions;

namespace Justina.Expense.Application.Receipts;

/// <summary>
/// Builds the instruction Vision is given, from the fixed base instruction plus the company's own
/// category and tax lists.
///
/// The Lambda does the same thing with <c>string.Format</c> over a template held in configuration
/// ("Category must be one of: {0} ... each entry must match a predefined tax in {1}"). Justina composes
/// it in code instead, for two reasons: the base instruction stays a compile-time constant that cannot
/// be altered by configuration, and every catalogue value is sanitized on the way in.
///
/// That sanitization is the point. The values come from our own API, but §38 says instructions are never
/// assembled out of data — a category someone named "ignore all previous instructions" must arrive as an
/// inert list item, not as a sentence the model can act on.
/// </summary>
public static class ReceiptExtractionPrompt
{
    /// <summary>
    /// Above these counts the catalogue stops being a useful constraint and starts being a cost and a
    /// context-window problem, so it is dropped entirely rather than silently truncated — a truncated
    /// list would quietly make the categories at the end unreachable.
    /// </summary>
    internal const int MaxCategories = 200;

    internal const int MaxTaxes = 50;

    /// <summary>ISO-4217 has 180 codes; a company claiming in more than that is not a real catalogue.</summary>
    internal const int MaxCurrencies = 180;

    /// <summary>Long enough for any real category name; short enough that no single entry can dominate.</summary>
    internal const int MaxEntryLength = 80;

    /// <summary>
    /// Returns the base instruction unchanged when there is nothing to constrain with, so a catalogue
    /// outage degrades to exactly today's behaviour rather than to a broken prompt.
    /// </summary>
    public static string Compose(ExpenseCatalogue? catalogue)
    {
        if (catalogue is null || catalogue.IsEmpty)
        {
            return ReceiptExtractionSchema.Instruction;
        }

        var categories = Sanitize(catalogue.Categories.Select(category => category.Name), MaxCategories);
        var taxes = Sanitize(catalogue.Taxes.Select(tax => tax.Label), MaxTaxes);
        var currencies = Sanitize(catalogue.Currencies.Select(currency => currency.Code), MaxCurrencies);

        if (categories.Count == 0 && taxes.Count == 0 && currencies.Count == 0)
        {
            return ReceiptExtractionSchema.Instruction;
        }

        var builder = new StringBuilder(ReceiptExtractionSchema.Instruction);

        if (currencies.Count > 0)
        {
            // The base instruction asks for the ISO code "if shown or unambiguous", which leaves every
            // receipt that prints a bare symbol or none at all with no currency — and a receipt with no
            // currency cannot be submitted. This is the resolution order the Lambda uses, kept in the
            // same sequence: explicit beats inferred, and nothing ever defaults to USD.
            builder
                .AppendLine()
                .AppendLine()
                .AppendLine("Currency must be one of the following ISO-4217 codes, or null:")
                .AppendLine(string.Join(", ", currencies))
                .AppendLine(
                    "Work through these in order and stop at the first that resolves. " +
                    "1) An ISO code printed on the receipt. " +
                    "2) A symbol that belongs to exactly one currency: S$ is SGD, RM is MYR, HK$ is HKD, " +
                    "US$ is USD, Rp is IDR, and a bare $ is ambiguous — do not treat it as USD, carry on. " +
                    "3) A tax line that implies a country: GST with a Singapore address is SGD, SST or " +
                    "service tax in Malaysia is MYR, VAT in the eurozone is EUR, GST in Australia is AUD. " +
                    "4) The country of the merchant's address. " +
                    "5) A telephone dialling code: +65 SGD, +60 MYR, +62 IDR, +852 HKD, +61 AUD, +66 THB, " +
                    "+63 PHP, +84 VND, +91 INR, +81 JPY, +82 KRW, +95 MMK, +44 GBP, +1 USD. " +
                    "6) A script used by only one country: Burmese is MMK, Thai is THB, Khmer is KHR, " +
                    "Hangul is KRW. Skip scripts shared by many countries, such as Latin or Arabic. " +
                    "7) If none of these resolves it, return null. Never guess, and never default to USD.");
        }

        if (categories.Count > 0)
        {
            builder
                .AppendLine()
                .AppendLine()
                .AppendLine("Category must be one of the following, copied exactly as written here:")
                .AppendLine(string.Join(", ", categories))
                .AppendLine(
                    "Choose the closest match by meaning. If nothing in the list fits, return null for " +
                    "category rather than inventing one, and never return a category that is not listed.");
        }

        if (taxes.Count > 0)
        {
            builder
                .AppendLine()
                .AppendLine("Taxes must each match one of the following predefined taxes, copied exactly:")
                .AppendLine(string.Join(", ", taxes))
                .AppendLine(
                    "Match on the RATE, not on the wording. The names above are the company's own and " +
                    "will often look nothing like the receipt: a receipt reading \"Inc 9% GST\", \"GST " +
                    "9%\", \"9% GST\" or plain \"GST\" at nine percent all match a predefined tax whose " +
                    "rate is 9.00, whatever that tax happens to be called. A tax printed as inclusive is " +
                    "still a tax: report it. " +
                    "Where an amount is printed with no rate, derive the rate from the amount divided by " +
                    "the pre-tax base, where that base is the subtotal plus any service charge listed " +
                    "above the tax line, and match the predefined tax with that rate. " +
                    "Return an empty list only when the receipt genuinely shows no tax, or when its rate " +
                    "matches none of the rates above. Report taxAmount separately as the amount printed " +
                    "on the receipt.");
        }

        return builder.ToString().TrimEnd();
    }

    /// <summary>
    /// Makes a catalogue value safe to place inside an instruction: no line breaks or control characters
    /// that could fake a new instruction block, no braces that could look like a placeholder, length
    /// capped, blanks and duplicates dropped. Returns an empty list when the catalogue is over its cap.
    /// </summary>
    private static List<string> Sanitize(IEnumerable<string> values, int maxEntries)
    {
        var sanitized = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var value in values)
        {
            var entry = Clean(value);

            if (entry is null || !seen.Add(entry))
            {
                continue;
            }

            sanitized.Add(entry);

            if (sanitized.Count > maxEntries)
            {
                return [];
            }
        }

        return sanitized;
    }

    private static string? Clean(string? value)
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
                if (!lastWasSpace && builder.Length > 0)
                {
                    builder.Append(' ');
                    lastWasSpace = true;
                }

                continue;
            }

            // Braces are dropped so a name can never read as a format placeholder, and commas are dropped
            // because the list itself is comma-separated — a name containing one would split into two.
            if (character is '{' or '}' or ',')
            {
                continue;
            }

            builder.Append(character);
            lastWasSpace = false;
        }

        var cleaned = builder.ToString().Trim();

        if (cleaned.Length == 0)
        {
            return null;
        }

        return cleaned.Length > MaxEntryLength
            ? cleaned[..MaxEntryLength].TrimEnd()
            : cleaned;
    }

    /// <summary>
    /// The label the Expense API's own tax record produces. Kept here so the one place that builds a
    /// label from parts agrees with the API's <c>Name (Rate%)</c> rendering, including its decimal scale.
    /// </summary>
    public static string TaxLabel(string name, decimal rate) =>
        string.Concat(name, " (", rate.ToString(CultureInfo.InvariantCulture), "%)");
}
