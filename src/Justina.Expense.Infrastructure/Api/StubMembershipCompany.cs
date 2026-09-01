using System.Text.Json;
using Justina.Expense.Infrastructure.MockData;

namespace Justina.Expense.Infrastructure.Api;

/// <summary>
/// The membership fixture, read once.
///
/// Two callers need it and neither should own it: the stub tenant resolver, which puts
/// <c>CompanyId</c> on the tenant it hands back, and <see cref="MockJustLoginCompanyDirectory"/>, which
/// answers the token request with it. One file, one reading, so the two can never disagree about which
/// company this is.
/// </summary>
internal static class StubMembershipCompany
{
    private static readonly Lazy<MockJustLoginCompanyDirectory.MembershipCompanyRecord?> Record = new(Load);

    public static MockJustLoginCompanyDirectory.MembershipCompanyRecord? Current => Record.Value;

    /// <returns>
    /// The company's <c>CompanyID</c>, or <c>null</c> when the fixture describes a different company or
    /// carries no id. Null rather than a fallback: a wrong CompanyID is accepted by the identity server
    /// and comes back as a token with no company claims, which fails much later and looks like an outage.
    /// </returns>
    public static string? CompanyIdFor(string companyGuid)
    {
        var record = Record.Value;

        if (record is null
            || string.IsNullOrWhiteSpace(record.CompanyId)
            || !string.Equals(record.CompanyGuid, companyGuid, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return record.CompanyId;
    }

    private static MockJustLoginCompanyDirectory.MembershipCompanyRecord? Load()
    {
        var json = MockDataResources.Read(MockDataResources.MembershipCompany);

        return json is null
            ? null
            : JsonSerializer.Deserialize<MockJustLoginCompanyDirectory.MembershipCompanyRecord>(
                json,
                new JsonSerializerOptions(JsonSerializerDefaults.Web));
    }
}
