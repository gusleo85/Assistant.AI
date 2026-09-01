using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Justina.Core.Domain.Messaging;

namespace Justina.Expense.Infrastructure.Api;

/// <summary>
/// One member exactly as <c>GET /expense/v1/Member/{memberId}</c> returns it. Kept verbatim — including
/// the API's own <c>isCompanySubcribedExpense</c> spelling — so the day this is swapped for a live call,
/// the shape is already the shape we have to parse.
/// </summary>
public sealed record StubMember
{
    [JsonPropertyName("id")]
    public Guid Id { get; init; }

    [JsonPropertyName("organizationId")]
    public Guid OrganizationId { get; init; }

    [JsonPropertyName("fullName")]
    public string? FullName { get; init; }

    [JsonPropertyName("email")]
    public string? Email { get; init; }

    [JsonPropertyName("employeeID")]
    public string? EmployeeId { get; init; }

    [JsonPropertyName("roleName")]
    public string? RoleName { get; init; }

    /// <summary>The 32-character uppercase form the membership API uses in its routes.</summary>
    public string CompanyGuid => OrganizationId.ToString("N").ToUpperInvariant();
}

/// <summary>
/// A channel identity mapped to a member. This is the link that does not exist yet in any JustLogin API:
/// a Telegram update carries a numeric user id and no phone number, so something has to record which
/// member that id belongs to. Here it is a file; live it will be a table Justina owns.
/// </summary>
public sealed record StubChannelLink
{
    [JsonPropertyName("channel")]
    public string Channel { get; init; } = string.Empty;

    [JsonPropertyName("userId")]
    public string UserId { get; init; } = string.Empty;

    [JsonPropertyName("memberId")]
    public Guid MemberId { get; init; }

    [JsonPropertyName("note")]
    public string? Note { get; init; }
}

/// <summary>
/// The mock member list and channel links, read once from resources embedded in this assembly so they
/// travel with the container image and need no volume mount.
/// </summary>
public sealed class StubMemberDirectory
{
    private const string MembersResource = "Justina.Expense.Infrastructure.MockData.members.json";
    private const string LinksResource = "Justina.Expense.Infrastructure.MockData.channel-links.json";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private static readonly Lazy<StubMemberDirectory> Instance = new(Load);

    private StubMemberDirectory(IReadOnlyList<StubMember> members, IReadOnlyList<StubChannelLink> links)
    {
        Members = members;
        Links = links;
    }

    public static StubMemberDirectory Current => Instance.Value;

    public IReadOnlyList<StubMember> Members { get; }

    public IReadOnlyList<StubChannelLink> Links { get; }

    /// <summary>The member a channel identity is linked to, or null when that identity is not paired.</summary>
    public StubMember? Find(ChannelKind channel, string userId)
    {
        var link = Links.FirstOrDefault(candidate =>
            string.Equals(candidate.Channel, channel.ToString(), StringComparison.OrdinalIgnoreCase)
            && string.Equals(candidate.UserId, userId, StringComparison.Ordinal));

        return link is null
            ? null
            : Members.FirstOrDefault(member => member.Id == link.MemberId);
    }

    /// <summary>
    /// Whoever a request falls back to when the channel identity is not linked. Stub mode has to keep
    /// working for an unpaired tester; the resolver logs loudly when this happens.
    /// </summary>
    public StubMember? Default => Members.Count > 0 ? Members[0] : null;

    private static StubMemberDirectory Load() =>
        new(Read<StubMember>(MembersResource), Read<StubChannelLink>(LinksResource));

    private static IReadOnlyList<T> Read<T>(string resourceName)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException(
                $"Embedded mock data '{resourceName}' is missing from {nameof(Justina.Expense.Infrastructure)}.");

        return JsonSerializer.Deserialize<List<T>>(stream, JsonOptions) ?? [];
    }
}
