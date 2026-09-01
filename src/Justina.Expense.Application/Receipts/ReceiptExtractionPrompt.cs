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

        if (categories.Count == 0 && taxes.Count == 0)
        {
            return ReceiptExtractionSchema.Instruction;
        }

        var builder = new StringBuilder(ReceiptExtractionSchema.Instruction);

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
                    "Return an empty list when the receipt shows no tax that matches one of them. " +
                    "Recognise a tax written as \"GST\", \"GST 9%\" or \"GST (9.00%)\" as the same tax. " +
                    "Where an amount is printed with no rate, derive the rate from the amount divided by " +
                    "the pre-tax base, where that base is the subtotal plus any service charge listed " +
                    "above the tax line, and match the closest predefined tax. If none is close, return " +
                    "an empty list. Report taxAmount separately as the amount printed on the receipt.");
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
