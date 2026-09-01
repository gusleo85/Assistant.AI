using System.Text.Json.Serialization;

namespace JustLogin.Identity.SDK.DTO.ApiResponses.v2.Membership;
public class MembershipCompanyResponse
{
    [JsonPropertyName("companyGuid")]
    public string CompanyGuid { get; set; }
    [JsonPropertyName("companyId")]
    public string CompanyId { get; set; }
    [JsonPropertyName("companyName")]
    public string CompanyName { get; set; }
}