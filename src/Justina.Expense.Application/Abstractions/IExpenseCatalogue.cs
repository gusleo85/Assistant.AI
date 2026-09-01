using Justina.Core.Application.Messaging;
using Justina.Core.Domain.Results;

namespace Justina.Expense.Application.Abstractions;

/// <summary>
/// One expense category as the Expense API defines it. <see cref="Name"/> is the only part the model
/// ever sees; <see cref="Id"/> is resolved in C# from the name the model answers with, so an id is
/// never something a model produced (§27, §34).
/// </summary>
public sealed record ExpenseCategory(Guid Id, string Name, string? AccountCode = null);

/// <summary>
/// One predefined tax. <see cref="Label"/> is stored rather than computed because it is the join key in
/// both directions — it is what the model is shown and what its answer is matched against — and it must
/// stay byte-identical to what the API produced, including the decimal scale of the rate
/// ("GST (9.00%)" is not the same string as "GST (9%)").
/// </summary>
public sealed record ExpenseTax(Guid Id, string Name, decimal Rate, string Label);

/// <summary>
/// The company's category and tax lists. Fetched per organization, never shared between them.
/// </summary>
public sealed record ExpenseCatalogue(
    IReadOnlyList<ExpenseCategory> Categories,
    IReadOnlyList<ExpenseTax> Taxes)
{
    /// <summary>What a caller gets when the catalogue could not be loaded. Extraction still proceeds.</summary>
    public static readonly ExpenseCatalogue Empty = new([], []);

    public bool IsEmpty => Categories.Count == 0 && Taxes.Count == 0;

    /// <summary>
    /// Resolves a category name the model answered with. Matching is case-insensitive and trimmed —
    /// deliberately more forgiving than the Lambda's ordinal <c>==</c>, which silently loses the id when
    /// the model changes one letter's case.
    /// </summary>
    public ExpenseCategory? FindCategory(string? name)
    {
        var candidate = Comparable(name);

        if (candidate is null)
        {
            return null;
        }

        return Categories.FirstOrDefault(
            category => string.Equals(Comparable(category.Name), candidate, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Resolves a tax label the model answered with, on the same terms as <see cref="FindCategory"/>.</summary>
    public ExpenseTax? FindTax(string? label)
    {
        var candidate = Comparable(label);

        if (candidate is null)
        {
            return null;
        }

        return Taxes.FirstOrDefault(
            tax => string.Equals(Comparable(tax.Label), candidate, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Normalizes a name for comparison: trimmed, with runs of whitespace collapsed to one space.
    ///
    /// The collapse is not cosmetic. Real catalogues contain names like "Employee Monthly Personal
    /// &#160;Expense" with a double space; the prompt shows that name with its whitespace collapsed, so
    /// the model copies back the single-spaced form. Comparing raw would then fail to resolve a name the
    /// model copied exactly as instructed.
    /// </summary>
    private static string? Comparable(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }
}

/// <summary>
/// Supplies the category and tax lists a receipt must be classified against.
///
/// A failure here is never fatal: the implementation returns <see cref="ExpenseCatalogue.Empty"/> and
/// extraction continues unconstrained, because a catalogue outage must not cost the user their receipt.
/// </summary>
public interface IExpenseCatalogue
{
    Task<ExpenseCatalogue> GetAsync(ExpenseTenant tenant, CancellationToken cancellationToken);
}

/// <summary>
/// Which JustLogin company a conversation acts for. The Lambda derives this from a receipt that already
/// exists; Justina has no such anchor, so it is resolved from the channel identity instead (see
/// <see cref="IExpenseTenantResolver"/>).
/// </summary>
public sealed record ExpenseTenant(Guid OrganizationId, string CompanyId, Guid MemberId)
{
    /// <summary>The 32-character uppercase form the membership API requires in its route.</summary>
    public string CompanyGuid => OrganizationId.ToString("N").ToUpperInvariant();
}

/// <summary>
/// Resolves the channel user behind a request to a JustLogin member and company.
///
/// WhatsApp supplies a phone number. Telegram supplies a numeric user id and no phone number, so it
/// requires a one-time link recorded on Justina's side before a conversation can submit anything.
/// </summary>
public interface IExpenseTenantResolver
{
    Task<Result<ExpenseTenant>> ResolveAsync(RequestContext context, CancellationToken cancellationToken);
}
