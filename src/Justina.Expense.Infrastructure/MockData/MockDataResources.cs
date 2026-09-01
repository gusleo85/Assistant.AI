
namespace Justina.Expense.Infrastructure.MockData;

/// <summary>
/// Reads the embedded mock data as raw JSON.
///
/// The stub seams deserialize these files; the mock HTTP endpoints serve the same bytes verbatim. Sharing
/// one source is the point: if Mock and Stub read different fixtures they would disagree about which
/// categories exist, and a receipt that maps cleanly in one mode would fail in the other for no reason a
/// developer could see.
/// </summary>
public static class MockDataResources
{
    private const string Prefix = "Justina.Expense.Infrastructure.MockData.";

    public const string Categories = "categories.json";
    public const string Taxes = "taxes.json";
    public const string Currencies = "currencies.json";
    public const string Members = "members.json";
    public const string ChannelLinks = "channel-links.json";
    public const string Organization = "organization.json";

    /// <summary>The membership API's company record — the companyGuid to CompanyID mapping.</summary>
    public const string MembershipCompany = "membership-company.json";

    /// <returns>The file's JSON, or <c>null</c> when no such resource is embedded.</returns>
    public static string? Read(string fileName)
    {
        var assembly = typeof(MockDataResources).Assembly;

        using var stream = assembly.GetManifestResourceStream(Prefix + fileName);

        if (stream is null)
        {
            return null;
        }

        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    /// <summary>Every embedded mock file, for diagnostics and for the mock endpoint's index.</summary>
    public static IReadOnlyList<string> Names =>
        typeof(MockDataResources).Assembly
            .GetManifestResourceNames()
            .Where(name => name.StartsWith(Prefix, StringComparison.Ordinal))
            .Select(name => name[Prefix.Length..])
            .Order(StringComparer.Ordinal)
            .ToList();

}
